using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using ModelContextProtocol.Server;
using UiPath.Engineering.Mcp.Core.Abstractions;
using UiPath.Engineering.Mcp.Core.CodeAnalysis;
using UiPath.Engineering.Mcp.Core.Models;

namespace UiPath.Engineering.Mcp.Tools;

[McpServerToolType]
public sealed class GetCompileErrorsTool {
    private readonly IFilesystemProvider _filesystem;
    private readonly ICSharpAnalysisService _analysis;

    public GetCompileErrorsTool(IFilesystemProvider filesystem, ICSharpAnalysisService analysis) {
        _filesystem = filesystem;
        _analysis = analysis;
    }

    [McpServerTool(
        UseStructuredContent = true,
        Title = "Get Compile Errors",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true),
     Description("Fast in-memory Roslyn diagnostics for .cs (not XAML). Not the green gate — that is validate_project. Next: edit_workflow_file.")]
    public async Task<ToolResult> GetCompileErrors(
        [Description("Absolute path to the UiPath project directory.")] string projectPath,
        [Description("Minimum severity to include: 'error' (default), 'warning', or 'all'.")]
        [AllowedValues("error", "warning", "all")] string? severity = null,
        CancellationToken cancellationToken = default) {
        var sw = Stopwatch.StartNew();

        if (ToolResults.GuardProject(_filesystem, projectPath, sw) is { } guardFailure) {
            return guardFailure;
        }

        if (!string.IsNullOrWhiteSpace(severity)
            && ToolArgs.ParseChoice(severity, "severity", ["error", "warning", "all"], sw, out _) is { } severityError) {
            return severityError;
        }

        var result = await _analysis.GetDiagnosticsAsync(projectPath, severity, cancellationToken);
        var summary = result.Diagnostics.Count == 0
            ? "No compiler diagnostics. Next: validate_project."
            : $"Found {result.Diagnostics.Count} compiler diagnostic(s). Next: edit_workflow_file.";
        return ToolResults.Ok(summary, result, sw, result.Warnings);
    }
}
