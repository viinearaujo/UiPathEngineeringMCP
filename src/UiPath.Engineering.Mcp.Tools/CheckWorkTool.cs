using System.ComponentModel;
using System.Diagnostics;
using ModelContextProtocol.Server;
using UiPath.Engineering.Mcp.Core.Abstractions;
using UiPath.Engineering.Mcp.Core.CodeAnalysis;
using UiPath.Engineering.Mcp.Core.Docs;
using UiPath.Engineering.Mcp.Core.GapAnalysis;
using UiPath.Engineering.Mcp.Core.Models;
using UiPath.Engineering.Mcp.Core.Parsing;
using UiPath.Engineering.Mcp.Core.Planning;
using UiPath.Engineering.Mcp.Providers.UiPathCli;

namespace UiPath.Engineering.Mcp.Tools;

/// <summary>
/// Single Copilot green-gate verdict: in-memory Roslyn compile + CLI validate + scoped gaps.
/// Waits on the dedicated validate path (does not require a second get_job poll).
/// </summary>
[McpServerToolType]
public sealed class CheckWorkTool {
    public const int MaxIssues = 20;

    private readonly IFilesystemProvider _filesystem;
    private readonly ICSharpAnalysisService _analysis;
    private readonly IUiPathCliProvider _cli;
    private readonly IProjectModelBuilder _modelBuilder;
    private readonly ImplementationPlanStore _planStore;
    private readonly ProjectDocsValidator _docsValidator;

    public CheckWorkTool(
        IFilesystemProvider filesystem,
        ICSharpAnalysisService analysis,
        IUiPathCliProvider cli,
        IProjectModelBuilder modelBuilder,
        ImplementationPlanStore planStore,
        ProjectDocsValidator docsValidator) {
        _filesystem = filesystem;
        _analysis = analysis;
        _cli = cli;
        _modelBuilder = modelBuilder;
        _planStore = planStore;
        _docsValidator = docsValidator;
    }

    [McpServerTool(
        UseStructuredContent = true,
        Title = "Check Work",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true),
     Description("One green-gate verdict: Roslyn .cs compile + validate_project(build:false,pack:false) + gaps (scoped when files set). Caps top 20 issues. Then update_plan_task. Next: update_plan_task.")]
    public async Task<ToolResult> CheckWork(
        [Description("Absolute path to the UiPath project directory.")] string projectPath,
        [Description("Optional project-relative files touched this turn. Empty = all .cs for compile and project-wide gaps.")] List<string>? files = null,
        CancellationToken cancellationToken = default) {
        var sw = Stopwatch.StartNew();
        if (ToolResults.GuardProject(_filesystem, projectPath, sw) is { } guardFailure) {
            return guardFailure;
        }

        var relativeFiles = (files ?? [])
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Select(f => f.Trim().Replace('\\', '/'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var compile = await _analysis.GetDiagnosticsAsync(projectPath, "error", cancellationToken);
        var compileIssues = compile.Diagnostics
            .Where(d => relativeFiles.Count == 0 || MatchesAny(d.FilePath, projectPath, relativeFiles))
            .Select(d => Issue("compile", d.Severity, $"{d.Code}: {d.Message}", RelativeOrRaw(projectPath, d.FilePath)))
            .ToList();

        var validate = await _cli.ValidateAsync(projectPath, validate: true, build: false, pack: false, cancellationToken);
        var validateIssues = validate.Errors
            .Select(e => Issue("validate", "error", e, null))
            .Concat(validate.Warnings.Select(w => Issue("validate", "warning", w, null)))
            .ToList();

        var model = await _modelBuilder.BuildAsync(projectPath, cancellationToken);
        _ = ToolResults.LoadPlanOrFail(_planStore, projectPath, sw, out var plan);
        var docsFindings = _docsValidator.Validate(projectPath, model);
        var gaps = ProjectGapAnalyzer.Analyze(model, plan, docsFindings, _filesystem)
            .Where(g => !string.Equals(g.Category, "docs", StringComparison.OrdinalIgnoreCase))
            .Where(g => relativeFiles.Count == 0
                || g.TargetFile is null
                || relativeFiles.Any(f => string.Equals(
                    Normalize(f), Normalize(g.TargetFile), StringComparison.OrdinalIgnoreCase)))
            .Where(g => g.Severity is Gap.Error or Gap.Warning)
            .Select(g => Issue("gap", g.Severity, g.Message, g.TargetFile))
            .ToList();

        var allIssues = compileIssues.Concat(validateIssues).Concat(gaps).ToList();
        var issues = allIssues.Take(MaxIssues).ToList();

        var ok = compileIssues.All(i => i.Severity != "error")
            && validate.Success
            && gaps.All(i => i.Severity != "error");

        return new ToolResult {
            Status = ok ? "success" : "error",
            Summary = ok
                ? "check_work passed. Next: update_plan_task."
                : $"check_work found {issues.Count} issue(s) (capped at {MaxIssues}). Next: fix, then check_work again.",
            Data = new {
                passed = ok,
                compileErrorCount = compileIssues.Count(i => i.Severity == "error"),
                validateSuccess = validate.Success,
                gapErrorCount = gaps.Count(i => i.Severity == "error"),
                scopedFiles = relativeFiles,
                issues,
                truncated = allIssues.Count > MaxIssues
            },
            Errors = issues.Where(i => i.Severity == "error").Select(i => $"[{i.Source}] {i.Message}").ToList(),
            Warnings = issues.Where(i => i.Severity == "warning").Select(i => $"[{i.Source}] {i.Message}").ToList(),
            DurationMs = sw.ElapsedMilliseconds
        };
    }

    private static CheckIssue Issue(string source, string severity, string message, string? file) =>
        new() { Source = source, Severity = severity, Message = message, File = file };

    private static bool MatchesAny(string diagnosticPath, string projectPath, IReadOnlyList<string> relatives) {
        var normalizedDiag = Normalize(RelativeOrRaw(projectPath, diagnosticPath));
        return relatives.Any(r => string.Equals(Normalize(r), normalizedDiag, StringComparison.OrdinalIgnoreCase)
            || normalizedDiag.EndsWith('/' + Normalize(r), StringComparison.OrdinalIgnoreCase)
            || normalizedDiag.EndsWith(Normalize(r), StringComparison.OrdinalIgnoreCase));
    }

    private static string RelativeOrRaw(string projectPath, string path) {
        if (string.IsNullOrWhiteSpace(path)) {
            return path;
        }

        try {
            var full = Path.GetFullPath(path);
            var root = Path.GetFullPath(projectPath);
            if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) {
                return Normalize(Path.GetRelativePath(root, full));
            }
        } catch {
            // Fall through.
        }

        return Normalize(path);
    }

    private static string Normalize(string path) => path.Replace('\\', '/').TrimStart('/');

    private sealed class CheckIssue {
        public string Source { get; init; } = string.Empty;
        public string Severity { get; init; } = string.Empty;
        public string Message { get; init; } = string.Empty;
        public string? File { get; init; }
    }
}
