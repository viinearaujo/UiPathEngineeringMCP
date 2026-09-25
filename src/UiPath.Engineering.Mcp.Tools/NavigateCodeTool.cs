using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using ModelContextProtocol.Server;
using UiPath.Engineering.Mcp.Core;
using UiPath.Engineering.Mcp.Core.Abstractions;
using UiPath.Engineering.Mcp.Core.CodeAnalysis;
using UiPath.Engineering.Mcp.Core.Models;

namespace UiPath.Engineering.Mcp.Tools;

[McpServerToolType]
public sealed class NavigateCodeTool {
    public const string ModeSymbol = "symbol";
    public const string ModeReferences = "references";
    public const string ModeContext = "context";

    private readonly IFilesystemProvider _filesystem;
    private readonly ICSharpAnalysisService _analysis;

    public NavigateCodeTool(IFilesystemProvider filesystem, ICSharpAnalysisService analysis) {
        _filesystem = filesystem;
        _analysis = analysis;
    }

    [McpServerTool(
        UseStructuredContent = true,
        Title = "Navigate Code",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true),
     Description("Roslyn C# navigation: mode=symbol finds a definition, mode=references finds usages, mode=context returns member signature/calls/source (by symbol or file+line). Prefer over reading whole .cs files. Next: edit_workflow_file.")]
    public async Task<ToolResult> NavigateCode(
        [Description("Absolute path to the UiPath project directory.")] string projectPath,
        [Description("Navigation mode: symbol, references, or context.")]
        [AllowedValues(ModeSymbol, ModeReferences, ModeContext)] string mode,
        [Description("Exact symbol name (required for symbol/references; optional for context).")] string? symbol = null,
        [Description("Optional kind filter for mode=symbol: method, property, field, class, interface.")]
        [AllowedValues("method", "property", "field", "class", "interface")] string? kind = null,
        [Description("For mode=context: .cs path used with line.")] string? file = null,
        [Description("For mode=context: 1-based line inside file; the enclosing member is returned.")] int? line = null,
        CancellationToken cancellationToken = default) {
        var sw = Stopwatch.StartNew();

        if (ToolResults.GuardProject(_filesystem, projectPath, sw) is { } guardFailure) {
            return guardFailure;
        }

        if (ToolArgs.ParseChoice(mode, "mode", [ModeSymbol, ModeReferences, ModeContext], sw, out var normalizedMode) is { } modeError) {
            return modeError;
        }

        return normalizedMode switch {
            ModeSymbol => await FindSymbol(projectPath, symbol, kind, sw, cancellationToken),
            ModeReferences => await FindReferences(projectPath, symbol, sw, cancellationToken),
            _ => await GetContext(projectPath, symbol, file, line, sw, cancellationToken)
        };
    }

    private async Task<ToolResult> FindSymbol(
        string projectPath,
        string? symbol,
        string? kind,
        Stopwatch sw,
        CancellationToken cancellationToken) {
        if (string.IsNullOrWhiteSpace(symbol)) {
            return ToolResults.Failure("symbol is required for mode=symbol.", sw);
        }

        if (!string.IsNullOrWhiteSpace(kind)
            && ToolArgs.ParseChoice(kind, "kind", ["method", "property", "field", "class", "interface"], sw, out _) is { } kindError) {
            return kindError;
        }

        var result = await _analysis.FindSymbolAsync(projectPath, symbol, kind, cancellationToken);
        var summary = result.Matches.Count == 0
            ? $"No symbols named '{symbol}' found."
            : $"Found {result.Matches.Count} symbol(s) named '{symbol}'. Next: navigate_code(mode=context).";
        return ToolResults.Ok(summary, result, sw, result.Warnings);
    }

    private async Task<ToolResult> FindReferences(
        string projectPath,
        string? symbol,
        Stopwatch sw,
        CancellationToken cancellationToken) {
        if (string.IsNullOrWhiteSpace(symbol)) {
            return ToolResults.Failure("symbol is required for mode=references.", sw);
        }

        var result = await _analysis.FindReferencesAsync(projectPath, symbol, cancellationToken);
        var summary = result.References.Count == 0
            ? $"No references to '{symbol}' found."
            : $"Found {result.References.Count} reference(s) to '{symbol}'. Next: edit_workflow_file.";
        return ToolResults.Ok(summary, result, sw, result.Warnings);
    }

    private async Task<ToolResult> GetContext(
        string projectPath,
        string? symbol,
        string? file,
        int? line,
        Stopwatch sw,
        CancellationToken cancellationToken) {
        var hasSymbol = !string.IsNullOrWhiteSpace(symbol);
        var hasFile = !string.IsNullOrWhiteSpace(file);
        if (!hasSymbol && !hasFile) {
            return ToolResults.Failure(new ToolError(
                ToolErrorCodes.InvalidArgument,
                "Pass either 'symbol' or 'file' + 'line' to locate the member.",
                "Pass symbol='ProcessTransaction', or file='Workflow.cs' with line=12."), sw);
        }

        if (hasFile && line is null) {
            return ToolResults.Failure(new ToolError(
                ToolErrorCodes.InvalidArgument,
                "'line' is required when 'file' is supplied.",
                "Pass the 1-based line number inside 'file', or locate the member by 'symbol' instead."), sw);
        }

        var result = await _analysis.GetCodeContextAsync(projectPath, symbol, file, line, cancellationToken);
        var summary = result.Found
            ? $"Context for '{result.Name}'. Next: edit_workflow_file."
            : "No matching member found.";
        return ToolResults.Ok(summary, result, sw, result.Warnings);
    }
}
