using System.ComponentModel;
using System.Diagnostics;
using ModelContextProtocol.Server;
using UiPath.Engineering.Mcp.Core.Abstractions;
using UiPath.Engineering.Mcp.Core.CodeAnalysis;
using UiPath.Engineering.Mcp.Core.Models;

namespace UiPath.Engineering.Mcp.Tools;

[McpServerToolType]
public sealed class GetCodeContextTool {
    private readonly IFilesystemProvider _filesystem;
    private readonly ICSharpAnalysisService _analysis;

    public GetCodeContextTool(IFilesystemProvider filesystem, ICSharpAnalysisService analysis) {
        _filesystem = filesystem;
        _analysis = analysis;
    }

    [McpServerTool, Description("Returns the semantic context of one C# member (a method, class, or property) in a UiPath project: signature, containing type, called methods, referenced types, and the member's source. Locate the member by 'symbol' name or by 'file' + 'line'. Prefer this over reading whole .cs files.")]
    public async Task<ToolResult> GetCodeContext(
        [Description("Absolute path to the UiPath project directory.")] string projectPath,
        [Description("Symbol name to inspect, e.g. 'ProcessTransaction'.")] string? symbol = null,
        [Description("Path of the .cs file (used with 'line').")] string? file = null,
        [Description("1-based line number inside 'file'; the enclosing member is returned.")] int? line = null,
        CancellationToken cancellationToken = default) {
        var sw = Stopwatch.StartNew();

        if (ToolResults.GuardProject(_filesystem, projectPath, sw) is { } guardFailure) {
            return guardFailure;
        }

        try {
            var result = await _analysis.GetCodeContextAsync(projectPath, symbol, file, line, cancellationToken);
            var summary = result.Found
                ? $"Context for '{result.Name}'."
                : "No matching member found.";
            return ToolResults.Ok(summary, result, sw, result.Warnings);
        } catch (Exception ex) {
            return ToolResults.FromException(ex, "Failed to get code context.", sw);
        }
    }
}
