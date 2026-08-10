using System.ComponentModel;
using System.Diagnostics;
using ModelContextProtocol.Server;
using UiPath.Engineering.Mcp.Core.Abstractions;
using UiPath.Engineering.Mcp.Core.CodeAnalysis;
using UiPath.Engineering.Mcp.Core.Models;

namespace UiPath.Engineering.Mcp.Tools;

[McpServerToolType]
public sealed class FindCodeReferencesTool {
    private readonly IFilesystemProvider _filesystem;
    private readonly ICSharpAnalysisService _analysis;

    public FindCodeReferencesTool(IFilesystemProvider filesystem, ICSharpAnalysisService analysis) {
        _filesystem = filesystem;
        _analysis = analysis;
    }

    [McpServerTool, Description("Finds all usage sites of a C# symbol (method, class, property, field) across a UiPath project's .cs files using Roslyn semantic analysis. When the symbol is not declared in project source, falls back to identifier matching and says so in the result.")]
    public async Task<ToolResult> FindCodeReferences(
        [Description("Absolute path to the UiPath project directory.")] string projectPath,
        [Description("Exact symbol name whose references to find, e.g. 'ProcessTransaction'.")] string symbol,
        CancellationToken cancellationToken = default) {
        var sw = Stopwatch.StartNew();

        if (ToolResults.GuardProject(_filesystem, projectPath, sw) is { } guardFailure) {
            return guardFailure;
        }

        try {
            var result = await _analysis.FindReferencesAsync(projectPath, symbol, cancellationToken);
            var summary = result.References.Count == 0
                ? $"No references to '{symbol}' found."
                : $"Found {result.References.Count} reference(s) to '{symbol}'.";
            return ToolResults.Ok(summary, result, sw, result.Warnings);
        } catch (Exception ex) {
            return ToolResults.FromException(ex, "Reference search failed.", sw);
        }
    }
}
