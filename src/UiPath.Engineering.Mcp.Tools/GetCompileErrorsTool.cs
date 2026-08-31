using System.ComponentModel;
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

    [McpServerTool(UseStructuredContent = true), Description("Fast in-memory C# compiler diagnostics (Roslyn) without a UiPath CLI build. Do not use for XAML, and do not use as the agent-loop green gate (that is validate_project). For an authoritative CLI build, call validate_project(build:true).")]
    public async Task<ToolResult> GetCompileErrors(
        [Description("Absolute path to the UiPath project directory.")] string projectPath,
        [Description("Minimum severity to include: 'error' (default), 'warning', or 'all'.")] string? severity = null,
        CancellationToken cancellationToken = default) {
        var sw = Stopwatch.StartNew();

        if (ToolResults.GuardProject(_filesystem, projectPath, sw) is { } guardFailure) {
            return guardFailure;
        }

        try {
            var result = await _analysis.GetDiagnosticsAsync(projectPath, severity, cancellationToken);
            var summary = result.Diagnostics.Count == 0
                ? "No compiler diagnostics."
                : $"Found {result.Diagnostics.Count} compiler diagnostic(s).";
            return ToolResults.Ok(summary, result, sw, result.Warnings);
        } catch (Exception ex) {
            return ToolResults.FromException(ex, "Failed to get compiler diagnostics.", sw);
        }
    }
}
