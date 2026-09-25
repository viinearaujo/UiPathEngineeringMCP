using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using ModelContextProtocol.Server;
using UiPath.Engineering.Mcp.Core;
using UiPath.Engineering.Mcp.Core.Abstractions;
using UiPath.Engineering.Mcp.Core.Docs;
using UiPath.Engineering.Mcp.Core.Models;
using UiPath.Engineering.Mcp.Core.Parsing;

namespace UiPath.Engineering.Mcp.Tools;

[McpServerToolType]
public sealed class ManageProjectContentTool {
    public const string ListDocs = "list_docs";
    public const string WriteDocs = "write_docs";
    public const string DeleteDocs = "delete_docs";
    public const string SearchDocs = "search_docs";
    public const string WriteFile = "write_file";
    public const string EditFile = "edit_file";
    public const string DeleteFile = "delete_file";
    public const string SyncContext = "sync_context";
    public const string ValidateDocs = "validate_docs";
    public const string ContextKind = "context";

    private static readonly string[] Actions = [
        ListDocs, WriteDocs, DeleteDocs, SearchDocs,
        WriteFile, EditFile, DeleteFile,
        SyncContext, ValidateDocs
    ];

    private readonly IFilesystemProvider _filesystem;
    private readonly ProjectKnowledgeStore _knowledge;
    private readonly ProjectAdrStore _adrs;
    private readonly ProjectDocsSearch _search;
    private readonly ProjectDocsValidator _validator;
    private readonly IProjectModelBuilder _modelBuilder;
    private readonly ProjectContextRenderer _renderer;

    public ManageProjectContentTool(
        IFilesystemProvider filesystem,
        ProjectKnowledgeStore knowledge,
        ProjectAdrStore adrs,
        ProjectDocsSearch search,
        ProjectDocsValidator validator,
        IProjectModelBuilder modelBuilder,
        ProjectContextRenderer renderer) {
        _filesystem = filesystem;
        _knowledge = knowledge;
        _adrs = adrs;
        _search = search;
        _validator = validator;
        _modelBuilder = modelBuilder;
        _renderer = renderer;
    }

    [McpServerTool(
        UseStructuredContent = true,
        Title = "Manage Project Content",
        ReadOnly = false,
        Destructive = true,
        Idempotent = false),
     Description("Project docs and files: list_docs/write_docs/delete_docs/search_docs (knowledge/ADRs), write_file/edit_file/delete_file (.md/.json/.txt; refuses project.json, plans, docs/knowledge|adr, secrets, ***REDACTED***), sync_context, validate_docs. Next: validate_project.")]
    public async Task<ToolResult> ManageProjectContent(
        [Description("Absolute path to the UiPath project directory (must contain project.json).")] string projectPath,
        [Description("Action: list_docs, write_docs, delete_docs, search_docs, write_file, edit_file, delete_file, sync_context, or validate_docs.")]
        [AllowedValues(
            ListDocs, WriteDocs, DeleteDocs, SearchDocs,
            WriteFile, EditFile, DeleteFile,
            SyncContext, ValidateDocs)] string action,
        [Description("Doc kind for *_docs actions: memory, adr, context, or all.")]
        [AllowedValues(ProjectKnowledgeStore.Kind, ProjectAdrStore.Kind, ContextKind, ProjectDocsSearch.KindAll)] string kind = ProjectDocsSearch.KindAll,
        [Description("Knowledge id (kebab-case) or ADR id (NNNN-slug). Required for delete_docs; optional for ADR update.")] string? id = null,
        [Description("Title for write_docs.")] string? title = null,
        [Description("Markdown body for write_docs, or full file content for write_file.")] string? content = null,
        [Description("Project-relative files this article/ADR describes.")] List<string>? relatedFiles = null,
        [Description("memory: current|deprecated. adr: proposed|accepted|superseded|deprecated.")] string? status = null,
        [Description("For a new ADR, id of the ADR this one supersedes.")] string? supersedes = null,
        [Description("Search query for search_docs.")] string? query = null,
        [Description("Path relative to the project root for *_file actions, e.g. 'docs/notes.md'.")] string? relativePath = null,
        [Description("For edit_file: exact text to find.")] string? oldString = null,
        [Description("For edit_file: replacement text.")] string? newString = null,
        CancellationToken cancellationToken = default) {

        var sw = Stopwatch.StartNew();

        if (ToolResults.GuardProject(_filesystem, projectPath, sw) is { } guardFailure) {
            return guardFailure;
        }

        if (ToolArgs.ParseChoice(action, "action", Actions, sw, out var normalizedAction) is { } actionError) {
            return actionError;
        }

        return normalizedAction switch {
            ListDocs or WriteDocs or DeleteDocs or SearchDocs => await HandleDocs(
                projectPath, normalizedAction, kind, id, title, content, relatedFiles, status, supersedes, query, sw, cancellationToken),
            WriteFile or EditFile or DeleteFile => HandleFile(
                projectPath, normalizedAction, relativePath, content, oldString, newString, sw),
            SyncContext => await SyncProjectContext(projectPath, sw, cancellationToken),
            _ => await ValidateProjectDocs(projectPath, sw, cancellationToken)
        };
    }

    private async Task<ToolResult> HandleDocs(
        string projectPath,
        string action,
        string kind,
        string? id,
        string? title,
        string? content,
        List<string>? relatedFiles,
        string? status,
        string? supersedes,
        string? query,
        Stopwatch sw,
        CancellationToken cancellationToken) {
        var kindChoices = new[] { ProjectKnowledgeStore.Kind, ProjectAdrStore.Kind, ContextKind, ProjectDocsSearch.KindAll };
        var kindValue = string.IsNullOrWhiteSpace(kind) ? ProjectDocsSearch.KindAll : kind;
        if (ToolArgs.ParseChoice(kindValue, "kind", kindChoices, sw, out var normalizedKind) is { } kindError) {
            return kindError;
        }

        return action switch {
            ListDocs => await ListDocsAsync(projectPath, normalizedKind, sw, cancellationToken),
            WriteDocs => WriteDocsAction(projectPath, normalizedKind, id, title, content, relatedFiles, status, supersedes, sw),
            DeleteDocs => DeleteDocsAction(projectPath, normalizedKind, id, sw),
            _ => SearchDocsAction(projectPath, normalizedKind, query, sw)
        };
    }

    private async Task<ToolResult> ListDocsAsync(string projectPath, string kind, Stopwatch sw, CancellationToken cancellationToken) {
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

    private ToolResult WriteDocsAction(
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
            return ToolResults.Failure("kind must be memory or adr for write_docs. Use action=sync_context for generated context.", sw);
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
            return ToolResults.Failure("kind must be memory or adr for write_docs.", sw);
        }

        if (string.IsNullOrWhiteSpace(id)) {
            return ToolResults.Failure("id is required for kind=memory.", sw);
        }

        var article = _knowledge.Upsert(projectPath, id, title ?? id, content ?? string.Empty, relatedFiles, status);
        return ToolResults.Ok($"Wrote knowledge article '{article.Id}'.", article, sw);
    }

    private ToolResult DeleteDocsAction(string projectPath, string kind, string? id, Stopwatch sw) {
        if (string.IsNullOrWhiteSpace(id)) {
            return ToolResults.Failure("id is required for delete_docs.", sw);
        }

        if (kind == ProjectAdrStore.Kind) {
            return _adrs.Delete(projectPath, id)
                ? ToolResults.Ok($"Deleted ADR '{id}'.", new { id, kind }, sw)
                : ToolResults.Failure($"ADR '{id}' was not found.", sw);
        }

        if (kind != ProjectKnowledgeStore.Kind) {
            return ToolResults.Failure("kind must be memory or adr for delete_docs.", sw);
        }

        return _knowledge.Delete(projectPath, id)
            ? ToolResults.Ok($"Deleted knowledge article '{id}'.", new { id, kind }, sw)
            : ToolResults.Failure($"Knowledge article '{id}' was not found.", sw);
    }

    private ToolResult SearchDocsAction(string projectPath, string kind, string? query, Stopwatch sw) {
        if (string.IsNullOrWhiteSpace(query)) {
            return ToolResults.Failure("query is required for search_docs.", sw);
        }

        var result = _search.Search(projectPath, query, kind);
        return ToolResults.Ok($"{result.Matches.Count} match(es).", result, sw, result.Warnings);
    }

    private ToolResult HandleFile(
        string projectPath,
        string action,
        string? relativePath,
        string? content,
        string? oldString,
        string? newString,
        Stopwatch sw) {
        if (string.IsNullOrWhiteSpace(relativePath)) {
            return ToolResults.Failure("relativePath is required.", sw);
        }

        if (!ToolResults.TryResolveWithinProject(projectPath, relativePath, out var targetPath)) {
            return ToolResults.Failure("relativePath must resolve to a location inside the project directory.", sw);
        }

        return action switch {
            WriteFile => WriteFileAction(relativePath, targetPath, content, sw),
            EditFile => EditFileAction(relativePath, targetPath, oldString, newString, sw),
            _ => DeleteFileAction(relativePath, targetPath, sw)
        };
    }

    private ToolResult WriteFileAction(string relativePath, string targetPath, string? content, Stopwatch sw) {
        var policyError = ProjectFilePolicy.ValidateMutatingFile(relativePath, content, requireContent: true);
        if (policyError is not null) {
            return ToolResults.Failure(policyError, sw);
        }

        var directory = Path.GetDirectoryName(targetPath)!;
        _filesystem.CreateDirectory(directory);
        _filesystem.WriteAllText(targetPath, content!);
        return ToolResults.Ok($"Wrote '{relativePath}'.", new { filePath = targetPath, action = WriteFile }, sw);
    }

    private ToolResult EditFileAction(string relativePath, string targetPath, string? oldString, string? newString, Stopwatch sw) {
        var policyError = ProjectFilePolicy.ValidateMutatingFile(relativePath, newString ?? string.Empty, requireContent: false);
        if (policyError is not null) {
            return ToolResults.Failure(policyError, sw);
        }

        if (string.IsNullOrEmpty(oldString)) {
            return ToolResults.Failure("oldString is required.", sw);
        }

        if (newString is null) {
            return ToolResults.Failure("newString is required.", sw);
        }

        if (ProjectFilePolicy.ContainsRedactedBody(newString)) {
            return ToolResults.Failure("newString contains ***REDACTED*** and must not be written back to disk.", sw);
        }

        if (!_filesystem.FileExists(targetPath)) {
            return ToolResults.Failure($"File '{relativePath}' does not exist in the project.", sw);
        }

        var original = _filesystem.ReadAllText(targetPath);
        var matches = 0;
        var index = 0;
        while ((index = original.IndexOf(oldString, index, StringComparison.Ordinal)) >= 0) {
            matches++;
            index += oldString.Length;
        }

        if (matches == 0) {
            return ToolResults.Failure("oldString was not found in the file.", sw);
        }

        if (matches > 1) {
            return ToolResults.Failure($"oldString matches {matches} locations; make it more specific.", sw);
        }

        var updated = original.Replace(oldString, newString, StringComparison.Ordinal);
        var afterPolicy = ProjectFilePolicy.ValidateMutatingFile(relativePath, updated, requireContent: true);
        if (afterPolicy is not null) {
            return ToolResults.Failure(afterPolicy, sw);
        }

        _filesystem.WriteAllText(targetPath, updated);
        return ToolResults.Ok($"Updated '{relativePath}'.", new { filePath = targetPath, action = EditFile, replacements = 1 }, sw);
    }

    private ToolResult DeleteFileAction(string relativePath, string targetPath, Stopwatch sw) {
        var policyError = ProjectFilePolicy.ValidateMutatingFile(relativePath, content: null, requireContent: false);
        if (policyError is not null) {
            return ToolResults.Failure(policyError, sw);
        }

        if (!_filesystem.FileExists(targetPath)) {
            return ToolResults.Failure($"File '{relativePath}' does not exist in the project.", sw);
        }

        _filesystem.DeleteFile(targetPath);
        return ToolResults.Ok($"Deleted '{relativePath}'.", new { filePath = targetPath, action = DeleteFile }, sw);
    }

    private async Task<ToolResult> SyncProjectContext(string projectPath, Stopwatch sw, CancellationToken cancellationToken) {
        var model = await _modelBuilder.BuildAsync(projectPath, cancellationToken);
        _renderer.Sync(projectPath, model);
        var (cs, xaml, deps) = ProjectContextRenderer.Counts(model);
        return ToolResults.Ok("Generated project context updated.", new {
            agentsMd = ProjectDocsPaths.AgentsMd(projectPath),
            projectContext = ProjectDocsPaths.ProjectContext(projectPath),
            counts = new { cs, xaml, deps }
        }, sw);
    }

    private async Task<ToolResult> ValidateProjectDocs(string projectPath, Stopwatch sw, CancellationToken cancellationToken) {
        var model = await _modelBuilder.BuildAsync(projectPath, cancellationToken);
        var findings = _validator.Validate(projectPath, model);
        var errors = findings.Count(f => f.Severity == DocsFinding.Error);
        var warnings = findings.Where(f => f.Severity == DocsFinding.Warning).Select(f => f.Message).ToList();
        var summary = errors == 0
            ? (warnings.Count == 0 ? "Project docs are current." : "Project docs have warnings only.")
            : $"Project docs have {errors} error finding(s).";

        if (errors > 0) {
            return ToolResults.Failure(summary, findings.Where(f => f.Severity == DocsFinding.Error).Select(DocsGate.ToToolError).ToList(), sw);
        }

        return ToolResults.Ok(summary, new { findings, errorCount = errors, warningCount = warnings.Count }, sw, warnings);
    }
}
