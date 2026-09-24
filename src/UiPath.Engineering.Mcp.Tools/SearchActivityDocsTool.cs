using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using UiPath.Engineering.Mcp.Core;
using UiPath.Engineering.Mcp.Core.Abstractions;
using UiPath.Engineering.Mcp.Core.Configuration;
using UiPath.Engineering.Mcp.Core.Knowledge;
using UiPath.Engineering.Mcp.Core.Models;

namespace UiPath.Engineering.Mcp.Tools;

/// <summary>
/// Excerpt search over activity documentation. The project-local
/// <c>{PROJECT_DIR}/.local/docs/packages</c> tree is searched first — those are the docs the
/// project's own installed packages ship, so they match the installed versions — and the
/// vendored uipath-rpa <c>activity-docs</c> snapshot is the fallback for everything else.
/// </summary>
[McpServerToolType]
public sealed class SearchActivityDocsTool {
    private const int DefaultMaxResults = 10;

    private readonly IFilesystemProvider _filesystem;
    private readonly SkillsOptions _skills;

    public SearchActivityDocsTool(IFilesystemProvider filesystem, IOptions<SkillsOptions> skills) {
        _filesystem = filesystem;
        _skills = skills.Value;
    }

    [McpServerTool(UseStructuredContent = true), Description("Searches per-activity documentation and returns the matching EXCERPTS with their file, line, and nearest heading — not just filenames. It reads the project's own installed package docs at {PROJECT_DIR}/.local/docs/packages first (version-matched to what the project actually references) and falls back to the vendored activity-docs snapshot shipped with the server. Use this instead of reading a whole document when you need one property, one enum value, or one behaviour note. Pass package to restrict the search to one package's docs. Next: get_activity_metadata.")]
    public Task<ToolResult> SearchActivityDocs(
        [Description("What to look for, e.g. 'MaxIterations', 'retry interval', 'selector not found', or an activity name like 'LogMessage'.")] string query,
        [Description("Optional absolute path to the UiPath project directory (must contain project.json). When set, the project's own installed package docs are searched first.")] string? projectPath = null,
        [Description("Optional package id to restrict the search, e.g. 'UiPath.System.Activities'. Matches a path segment in the documentation tree.")] string? package = null,
        [Description("Maximum excerpts to return (default 10, max 50).")] int? maxResults = null,
        CancellationToken cancellationToken = default) {

        var sw = Stopwatch.StartNew();

        if (string.IsNullOrWhiteSpace(projectPath) && string.IsNullOrWhiteSpace(query)) {
            return Task.FromResult(ToolResults.Failure("query is required.", sw));
        }

        if (!string.IsNullOrWhiteSpace(projectPath)
            && ToolResults.GuardProject(_filesystem, projectPath, sw) is { } guardFailure) {
            return Task.FromResult(guardFailure);
        }

        var corpora = new List<KnowledgeCorpus>();
        var projectRoot = KnowledgePaths.ResolveProjectActivityDocsRoot(projectPath);
        if (projectRoot is not null) {
            corpora.Add(new KnowledgeCorpus(projectRoot, KnowledgeSource.ProjectLocal));
        }

        var vendoredRoot = KnowledgePaths.ResolveActivityDocsRoot(_skills);
        if (vendoredRoot is not null) {
            corpora.Add(new KnowledgeCorpus(vendoredRoot, KnowledgeSource.Vendored));
        }

        if (corpora.Count == 0) {
            return Task.FromResult(ToolResults.Failure(new ToolError(
                ToolErrorCodes.OperationFailed,
                "No activity documentation corpus is available.",
                "Set Skills:SkillsRoot in appsettings.json to the directory containing uipath-rpa/, or pass projectPath for a project whose packages ship .local/docs."), sw));
        }

        var result = KnowledgeSearchEngine.Search(
            corpora, query, maxResults ?? DefaultMaxResults, package, cancellationToken);

        var warnings = new List<string>(result.Warnings);
        if (projectRoot is null) {
            warnings.Add("No project-local package docs were found (pass projectPath, or install a package that ships .local/docs), so only the vendored snapshot was searched.");
        }

        if (result.Excerpts.Count == 0) {
            warnings.Add($"No documentation matched '{query}'. Try a shorter term (a property name or enum value), drop the package filter, or call recommend_activities to confirm the activity name.");
        }

        return Task.FromResult(ToolResults.Ok(
            result.Excerpts.Count == 0
                ? $"No activity documentation matched '{query}'."
                : $"{result.Excerpts.Count} documentation excerpt(s) for '{query}' across {result.FilesSearched} file(s).",
            new {
                query,
                package,
                sourcesSearched = result.RootsSearched,
                filesSearched = result.FilesSearched,
                excerpts = result.Excerpts.Select(ToPayload).ToList(),
                note = result.Note
            }, sw, warnings));
    }

    internal static object ToPayload(KnowledgeExcerpt excerpt) => new {
        source = excerpt.Source,
        package = excerpt.Package,
        activity = excerpt.Activity,
        relativePath = excerpt.RelativePath,
        filePath = excerpt.FilePath,
        line = excerpt.Line,
        heading = excerpt.Heading,
        snippet = excerpt.Snippet
    };
}