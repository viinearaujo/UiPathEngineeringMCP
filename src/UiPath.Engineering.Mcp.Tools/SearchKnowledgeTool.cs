using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using UiPath.Engineering.Mcp.Core;
using UiPath.Engineering.Mcp.Core.Abstractions;
using UiPath.Engineering.Mcp.Core.Configuration;
using UiPath.Engineering.Mcp.Core.Knowledge;
using UiPath.Engineering.Mcp.Core.Models;
using UiPath.Engineering.Mcp.Providers.Skills;

namespace UiPath.Engineering.Mcp.Tools;

[McpServerToolType]
public sealed class SearchKnowledgeTool {
    public const string ModeActivityDocs = "activity_docs";
    public const string ModeKnowledge = "knowledge";
    public const string ModeSkill = "skill";

    internal const string KnowledgeAll = "all";
    internal const string KnowledgeGuides = "guides";
    internal const string KnowledgeActivityDocs = "activityDocs";
    internal static readonly string[] KnowledgeKinds = [KnowledgeAll, KnowledgeGuides, KnowledgeActivityDocs];

    private const int DefaultActivityDocsMax = 10;
    private const int DefaultKnowledgeMax = 12;

    private readonly IFilesystemProvider _filesystem;
    private readonly SkillsOptions _skills;
    private readonly ISkillsProvider _skillsProvider;

    public SearchKnowledgeTool(
        IFilesystemProvider filesystem,
        IOptions<SkillsOptions> skills,
        ISkillsProvider skillsProvider) {
        _filesystem = filesystem;
        _skills = skills.Value;
        _skillsProvider = skillsProvider;
    }

    [McpServerTool(
        UseStructuredContent = true,
        Title = "Search Knowledge",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true),
     Description("RPA knowledge lookup: mode=activity_docs (package docs excerpts), mode=knowledge (guides+activity corpus excerpts; kind=all|guides|activityDocs), mode=skill (read one SKILL.md or skill file). Next: get_activity_metadata or validate_project.")]
    public async Task<ToolResult> SearchKnowledge(
        [Description("Lookup mode: activity_docs, knowledge, or skill.")]
        [AllowedValues(ModeActivityDocs, ModeKnowledge, ModeSkill)] string mode,
        [Description("Search query (activity_docs/knowledge) or skill name (skill), e.g. 'uipath-rpa'.")] string? query = null,
        [Description("Optional project path for activity_docs (searches .local/docs/packages first).")] string? projectPath = null,
        [Description("Optional package id filter for activity_docs, e.g. 'UiPath.System.Activities'.")] string? package = null,
        [Description("For mode=knowledge: all (default), guides, or activityDocs.")]
        [AllowedValues(KnowledgeAll, KnowledgeGuides, KnowledgeActivityDocs)] string kind = KnowledgeAll,
        [Description("For mode=skill: optional file under the skill, e.g. 'references/auth.md'. Defaults to SKILL.md.")] string? file = null,
        [Description("Maximum excerpts to return (activity_docs default 10, knowledge default 12, max 50).")] int? maxResults = null,
        CancellationToken cancellationToken = default) {

        var sw = Stopwatch.StartNew();

        if (ToolArgs.ParseChoice(mode, "mode", [ModeActivityDocs, ModeKnowledge, ModeSkill], sw, out var normalizedMode) is { } modeError) {
            return modeError;
        }

        return normalizedMode switch {
            ModeActivityDocs => SearchActivityDocs(query, projectPath, package, maxResults, sw, cancellationToken),
            ModeKnowledge => SearchUiPathKnowledge(query, kind, maxResults, sw, cancellationToken),
            _ => await ReadSkill(query, file, sw, cancellationToken)
        };
    }

    private ToolResult SearchActivityDocs(
        string? query,
        string? projectPath,
        string? package,
        int? maxResults,
        Stopwatch sw,
        CancellationToken cancellationToken) {
        if (string.IsNullOrWhiteSpace(projectPath) && string.IsNullOrWhiteSpace(query)) {
            return ToolResults.Failure("query is required.", sw);
        }

        if (!string.IsNullOrWhiteSpace(projectPath)
            && ToolResults.GuardProject(_filesystem, projectPath, sw) is { } guardFailure) {
            return guardFailure;
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
            return ToolResults.Failure(new ToolError(
                ToolErrorCodes.OperationFailed,
                "No activity documentation corpus is available.",
                "Set Skills:SkillsRoot in appsettings.json to the directory containing uipath-rpa/, or pass projectPath for a project whose packages ship .local/docs."), sw);
        }

        var result = KnowledgeSearchEngine.Search(
            corpora, query ?? string.Empty, maxResults ?? DefaultActivityDocsMax, package, cancellationToken);

        var warnings = new List<string>(result.Warnings);
        if (projectRoot is null) {
            warnings.Add("No project-local package docs were found (pass projectPath, or install a package that ships .local/docs), so only the vendored snapshot was searched.");
        }

        if (result.Excerpts.Count == 0) {
            warnings.Add($"No documentation matched '{query}'. Try a shorter term (a property name or enum value), drop the package filter, or call recommend_activities to confirm the activity name.");
        }

        return ToolResults.Ok(
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
            }, sw, warnings);
    }

    private ToolResult SearchUiPathKnowledge(
        string? query,
        string kind,
        int? maxResults,
        Stopwatch sw,
        CancellationToken cancellationToken) {
        if (string.IsNullOrWhiteSpace(query)) {
            return ToolResults.Failure("query is required for mode=knowledge.", sw);
        }

        if (!string.IsNullOrWhiteSpace(kind)
            && ToolArgs.ParseChoice(kind, "kind", KnowledgeKinds, sw, out _) is { } kindError) {
            return kindError;
        }

        var corpora = BuildKnowledgeCorpora(kind);
        if (corpora.Count == 0) {
            return ToolResults.Failure(new ToolError(
                ToolErrorCodes.SkillsRootMissing,
                "The skills knowledge corpus was not found.",
                "Set Skills:SkillsRoot in appsettings.json to the directory containing uipath-rpa/ (default '.agents/skills')."), sw);
        }

        var result = KnowledgeSearchEngine.Search(
            corpora, query, maxResults ?? DefaultKnowledgeMax, packageFilter: null, cancellationToken);

        var warnings = new List<string>(result.Warnings);
        if (result.Excerpts.Count == 0) {
            warnings.Add($"No knowledge base content matched '{query}'. Try a shorter or more specific term; search_knowledge(mode=skill) remains available when you know the path.");
        }

        return ToolResults.Ok(
            result.Excerpts.Count == 0
                ? $"No knowledge base content matched '{query}'."
                : $"{result.Excerpts.Count} excerpt(s) for '{query}' across {result.FilesSearched} file(s).",
            new {
                query,
                kind,
                sourcesSearched = result.RootsSearched,
                filesSearched = result.FilesSearched,
                excerpts = result.Excerpts.Select(ToPayload).ToList(),
                note = result.Note
            }, sw, warnings);
    }

    private async Task<ToolResult> ReadSkill(
        string? name,
        string? file,
        Stopwatch sw,
        CancellationToken cancellationToken) {
        if (string.IsNullOrWhiteSpace(name)) {
            return ToolResults.Failure("query (skill name) is required for mode=skill.", sw);
        }

        var result = await _skillsProvider.ReadAsync(name, file, cancellationToken);
        if (!result.Success) {
            return ToolResults.Failure(result.ErrorMessage ?? "Skill read failed.", [MapSkillError(result)], sw);
        }

        var (redacted, redactedCount) = SecretRedactor.Redact(result.Content);
        return ToolResults.Ok($"Read '{result.File}' from skill '{result.SkillName}'.",
            new {
                name = result.SkillName,
                file = result.File,
                content = redacted,
                truncated = result.Truncated,
                redactedCount
            }, sw);
    }

    private List<KnowledgeCorpus> BuildKnowledgeCorpora(string kind) {
        var corpora = new List<KnowledgeCorpus>();
        var wantsGuides = !string.Equals(kind, KnowledgeActivityDocs, StringComparison.OrdinalIgnoreCase);
        var wantsActivityDocs = !string.Equals(kind, KnowledgeGuides, StringComparison.OrdinalIgnoreCase);

        if (wantsGuides && KnowledgePaths.ResolveReferenceGuidesRoot(_skills) is { } guides) {
            corpora.Add(new KnowledgeCorpus(guides, KnowledgeSource.Vendored));
        } else if (wantsActivityDocs && KnowledgePaths.ResolveActivityDocsRoot(_skills) is { } activityDocs) {
            corpora.Add(new KnowledgeCorpus(activityDocs, KnowledgeSource.Vendored));
        }

        return corpora;
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

    private static ToolError MapSkillError(SkillReadResult result) => result.ErrorCode switch {
        "SKILL_NOT_FOUND" => new ToolError(ToolErrorCodes.SkillNotFound, result.ErrorMessage!,
            $"Pick one of the available skills: {string.Join(", ", result.AvailableSkills)}.", "list_skills"),
        "SKILL_PATH_REJECTED" => new ToolError(ToolErrorCodes.SkillPathRejected, result.ErrorMessage!,
            "Pass a file path inside the skill directory, without '..' or absolute paths."),
        "SKILL_FILE_NOT_FOUND" => new ToolError(ToolErrorCodes.SkillFileNotFound, result.ErrorMessage!,
            "Check the file name against the skill directory contents; default is SKILL.md."),
        _ => new ToolError(ToolErrorCodes.SkillsRootMissing, result.ErrorMessage!,
            "Set Skills:SkillsRoot in appsettings.json to a directory containing */SKILL.md.")
    };
}
