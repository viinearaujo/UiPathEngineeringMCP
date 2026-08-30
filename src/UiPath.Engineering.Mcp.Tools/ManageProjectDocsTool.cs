using System.ComponentModel;
using System.Diagnostics;
using ModelContextProtocol.Server;
using UiPath.Engineering.Mcp.Core;
using UiPath.Engineering.Mcp.Core.Abstractions;
using UiPath.Engineering.Mcp.Core.Docs;
using UiPath.Engineering.Mcp.Core.Models;
using UiPath.Engineering.Mcp.Core.Parsing;

namespace UiPath.Engineering.Mcp.Tools;

[McpServerToolType]
public sealed class ManageProjectDocsTool {
    public const string List = "list";
    public const string Write = "write";
    public const string Delete = "delete";
    public const string Search = "search";

    private readonly IFilesystemProvider _filesystem;
    private readonly ProjectKnowledgeStore _knowledge;
    private readonly ProjectAdrStore _adrs;
    private readonly ProjectDocsSearch _search;
    private readonly ProjectDocsValidator _validator;
    private readonly IProjectModelBuilder _modelBuilder;

    public ManageProjectDocsTool(
        IFilesystemProvider filesystem,
        ProjectKnowledgeStore knowledge,
        ProjectAdrStore adrs,
        ProjectDocsSearch search,
        ProjectDocsValidator validator,
        IProjectModelBuilder modelBuilder) {
        _filesystem = filesystem;
        _knowledge = knowledge;
        _adrs = adrs;
        _search = search;
        _validator = validator;
        _modelBuilder = modelBuilder;
    }

    [McpServerTool(UseStructuredContent = true), Description("Manages project docs-as-code: list, write, delete, or search knowledge articles and ADRs. kind is memory, adr, context, or all. Search is keyword/excerpt only.")]
    public async Task<ToolResult> ManageProjectDocs(
        [Description("Absolute path to the UiPath project directory (must contain project.json).")] string projectPath,
        [Description("Operation: list, write, delete, or search.")] string action,
        [Description("Doc kind: memory, adr, context, or all.")] string kind = ProjectDocsSearch.KindAll,
        [Description("Knowledge id (kebab-case) or ADR id (NNNN-slug). Required for delete; optional for ADR update.")] string? id = null,
        [Description("Title for write.")] string? title = null,
        [Description("Markdown body for write. ADRs must include Context, Decision, and Consequences headings.")] string? content = null,
        [Description("Project-relative files this article/ADR describes.")] List<string>? relatedFiles = null,
        [Description("memory: current|deprecated. adr: proposed|accepted|superseded|deprecated.")] string? status = null,
        [Description("For a new ADR, id of the ADR this one supersedes.")] string? supersedes = null,
        [Description("Search query for action=search.")] string? query = null,
        CancellationToken cancellationToken = default) {

        var sw = Stopwatch.StartNew();

        if (ToolResults.GuardProject(_filesystem, projectPath, sw) is { } guardFailure) {
            return guardFailure;
        }

        var normalizedAction = action?.Trim().ToLowerInvariant();
        var normalizedKind = string.IsNullOrWhiteSpace(kind) ? ProjectDocsSearch.KindAll : kind.Trim().ToLowerInvariant();

        try {
            return normalizedAction switch {
                List => await ListDocs(projectPath, normalizedKind, sw, cancellationToken),
                Write => WriteDocs(projectPath, normalizedKind, id, title, content, relatedFiles, status, supersedes, sw),
                Delete => DeleteDocs(projectPath, normalizedKind, id, sw),
                Search => SearchDocs(projectPath, normalizedKind, query, sw),
                _ => ToolResults.Failure("action must be list, write, delete, or search.", sw)
            };
        } catch (ArgumentException ex) {
            return ToolResults.Failure(ex.Message, sw);
        } catch (Exception ex) {
            return ToolResults.FromException(ex, "Project docs operation failed.", sw);
        }
    }

    private async Task<ToolResult> ListDocs(string projectPath, string kind, Stopwatch sw, CancellationToken cancellationToken) {
        UiPathProjectModel? model = null;
        try {
            model = await _modelBuilder.BuildAsync(projectPath, cancellationToken);
        } catch (Exception) {
            // Listing still works off the stores if the model cannot be built.
        }

        var findings = model is null ? [] : _validator.Validate(projectPath, model);
        var memory = kind is ProjectKnowledgeStore.Kind or ProjectDocsSearch.KindAll
            ? DescribeKnowledge(projectPath, findings)
            : null;
        var adrs = kind is ProjectAdrStore.Kind or ProjectDocsSearch.KindAll
            ? DescribeAdrs(projectPath, findings)
            : null;

        return ToolResults.Ok("Project docs listed.", new {
            kind,
            memory,
            adrs,
            context = new {
                agentsMd = _filesystem.FileExists(ProjectDocsPaths.AgentsMd(projectPath)),
                projectContext = _filesystem.FileExists(ProjectDocsPaths.ProjectContext(projectPath)),
                errorFindings = findings.Count(f => f.Severity == DocsFinding.Error),
                warningFindings = findings.Count(f => f.Severity == DocsFinding.Warning)
            }
        }, sw);
    }

    private object DescribeKnowledge(string projectPath, List<DocsFinding> findings) {
        var index = _knowledge.Load(projectPath);
        return index.Articles.Select(a => new {
            a.Id,
            a.Title,
            a.Status,
            a.RelatedFiles,
            a.UpdatedUtc,
            stale = findings.Any(f => f.Code == ToolErrorCodes.DocsStale && f.Message.Contains($"'{a.Id}'", StringComparison.Ordinal)),
            missing = findings.Any(f => f.Code == ToolErrorCodes.DocsInconsistent && string.Equals(f.TargetFile, a.FileName, StringComparison.OrdinalIgnoreCase))
        }).ToList();
    }

    private object DescribeAdrs(string projectPath, List<DocsFinding> findings) {
        var index = _adrs.Load(projectPath);
        return index.Adrs.Select(a => new {
            a.Id,
            a.Number,
            a.Title,
            a.Status,
            a.RelatedFiles,
            a.UpdatedUtc,
            a.Supersedes,
            stale = findings.Any(f => f.Code == ToolErrorCodes.DocsStale && f.Message.Contains($"'{a.Id}'", StringComparison.Ordinal)),
            missing = findings.Any(f => f.Code == ToolErrorCodes.DocsInconsistent && string.Equals(f.TargetFile, a.FileName, StringComparison.OrdinalIgnoreCase))
        }).ToList();
    }

    private ToolResult WriteDocs(
        string projectPath,
        string kind,
        string? id,
        string? title,
        string? content,
        List<string>? relatedFiles,
        string? status,
        string? supersedes,
        Stopwatch sw) {

        if (kind is ProjectDocsSearch.KindContext or ProjectDocsSearch.KindAll) {
            return ToolResults.Failure("kind must be memory or adr for write. Use sync_project_context for generated context.", sw);
        }

        if (kind == ProjectAdrStore.Kind) {
            var markdown = content;
            if (string.IsNullOrWhiteSpace(markdown)) {
                markdown = ProjectAdrStore.RenderTemplate(title ?? "Untitled", status ?? AdrRecord.Proposed, null, null, null);
            }

            var record = _adrs.Write(projectPath, title ?? "Untitled", markdown, relatedFiles, status, supersedes, id);
            return ToolResults.Ok($"Wrote ADR '{record.Id}'.", record, sw);
        }

        if (kind != ProjectKnowledgeStore.Kind) {
            return ToolResults.Failure("kind must be memory or adr for write.", sw);
        }

        if (string.IsNullOrWhiteSpace(id)) {
            return ToolResults.Failure("id is required for kind=memory.", sw);
        }

        var article = _knowledge.Upsert(projectPath, id, title ?? id, content ?? string.Empty, relatedFiles, status);
        return ToolResults.Ok($"Wrote knowledge article '{article.Id}'.", article, sw);
    }

    private ToolResult DeleteDocs(string projectPath, string kind, string? id, Stopwatch sw) {
        if (string.IsNullOrWhiteSpace(id)) {
            return ToolResults.Failure("id is required for delete.", sw);
        }

        if (kind == ProjectAdrStore.Kind) {
            return _adrs.Delete(projectPath, id)
                ? ToolResults.Ok($"Deleted ADR '{id}'.", new { id, kind }, sw)
                : ToolResults.Failure($"ADR '{id}' was not found.", sw);
        }

        if (kind != ProjectKnowledgeStore.Kind) {
            return ToolResults.Failure("kind must be memory or adr for delete.", sw);
        }

        return _knowledge.Delete(projectPath, id)
            ? ToolResults.Ok($"Deleted knowledge article '{id}'.", new { id, kind }, sw)
            : ToolResults.Failure($"Knowledge article '{id}' was not found.", sw);
    }

    private ToolResult SearchDocs(string projectPath, string kind, string? query, Stopwatch sw) {
        if (string.IsNullOrWhiteSpace(query)) {
            return ToolResults.Failure("query is required for search.", sw);
        }

        var result = _search.Search(projectPath, query, kind);
        return ToolResults.Ok($"{result.Matches.Count} match(es).", result, sw, result.Warnings);
    }
}
