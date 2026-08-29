using System.ComponentModel;
using System.Diagnostics;
using ModelContextProtocol.Server;
using UiPath.Engineering.Mcp.Core.Abstractions;
using UiPath.Engineering.Mcp.Core.CodeAnalysis;
using UiPath.Engineering.Mcp.Core.Models;

namespace UiPath.Engineering.Mcp.Tools;

[McpServerToolType]
public sealed class FindCodeSymbolTool {
    private readonly IFilesystemProvider _filesystem;
    private readonly ICSharpAnalysisService _analysis;

    public FindCodeSymbolTool(IFilesystemProvider filesystem, ICSharpAnalysisService analysis) {
        _filesystem = filesystem;
        _analysis = analysis;
    }

    [McpServerTool(UseStructuredContent = true), Description("Finds a C# symbol definition by exact name (method, class, property, field, interface) using Roslyn. Prefer this over reading whole .cs files. Do not use for substring search across XAML or text — that is search_codebase.")]
    public async Task<ToolResult> FindCodeSymbol(
        [Description("Absolute path to the UiPath project directory.")] string projectPath,
        [Description("Exact symbol name to find, e.g. 'ProcessTransaction'.")] string symbol,
        [Description("Optional kind filter: method, property, field, class, interface.")] string? kind = null,
        CancellationToken cancellationToken = default) {
        var sw = Stopwatch.StartNew();

        if (ToolResults.GuardProject(_filesystem, projectPath, sw) is { } guardFailure) {
            return guardFailure;
        }

        try {
            var result = await _analysis.FindSymbolAsync(projectPath, symbol, kind, cancellationToken);
            var summary = result.Matches.Count == 0
                ? $"No symbols named '{symbol}' found."
                : $"Found {result.Matches.Count} symbol(s) named '{symbol}'.";
            return ToolResults.Ok(summary, result, sw, result.Warnings);
        } catch (Exception ex) {
            return ToolResults.FromException(ex, "Symbol search failed.", sw);
        }
    }
}
