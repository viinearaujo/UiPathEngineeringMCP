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

    [McpServerTool, Description("Returns structured C# compiler diagnostics (Roslyn) for a UiPath project without running a build: file, line, column, code, severity, message. Fast and in-memory. Use compile_project for the authoritative UiPath CLI build result.")]
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
