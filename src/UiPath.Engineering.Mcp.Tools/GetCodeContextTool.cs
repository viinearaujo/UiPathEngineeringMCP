using System.ComponentModel;
using System.Diagnostics;
using ModelContextProtocol.Server;
using UiPath.Engineering.Mcp.Core;
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

    [McpServerTool(UseStructuredContent = true), Description("Returns the semantic context of one C# member (a method, class, or property) in a UiPath project: signature, containing type, called methods, referenced types, and the member's source. Locate the member by 'symbol' name or by 'file' + 'line'. Prefer this over reading whole .cs files. Next: edit_workflow_file.")]
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

        // Both locators are optional in the schema, so an all-null call would reach the
        // analyzer and return "Found: false" with no hint about the missing input. Reject it
        // as an argument error instead, and require file+line as a pair.
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
