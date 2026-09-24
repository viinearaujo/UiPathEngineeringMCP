using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using ModelContextProtocol.Server;
using UiPath.Engineering.Mcp.Core;
using UiPath.Engineering.Mcp.Core.Abstractions;
using UiPath.Engineering.Mcp.Core.CodeSearch;
using UiPath.Engineering.Mcp.Core.Models;

namespace UiPath.Engineering.Mcp.Tools;

[McpServerToolType]
public sealed class SearchCodebaseTool {
    private readonly IFilesystemProvider _filesystem;
    private readonly ICodebaseSearchService _search;

    public SearchCodebaseTool(IFilesystemProvider filesystem, ICodebaseSearchService search) {
        _filesystem = filesystem;
        _search = search;
    }

    [McpServerTool(UseStructuredContent = true), Description("Substring search across a UiPath project's .xaml and .cs files. Modes: 'text' (matching lines), 'symbol' (C# name contains query), 'activity' (XAML display name/type), 'workflow' (file name/description). Do not use for exact C# definition or usage lookup — that is find_code_symbol / find_code_references. Next: read_workflow_file or find_activity.")]
    public async Task<ToolResult> SearchCodebase(
        [Description("Absolute path to the UiPath project directory.")] string projectPath,
        [Description("Case-insensitive substring to search for, e.g. 'queue'.")] string query,
        [Description("Search mode: text, symbol, activity, or workflow.")]
        [AllowedValues("text", "symbol", "activity", "workflow")] string mode,
        [Description("Optional kind filter for symbol mode: method, property, field, class, interface.")]
        [AllowedValues("method", "property", "field", "class", "interface")] string? kind = null,
        CancellationToken cancellationToken = default) {
        var sw = Stopwatch.StartNew();

        if (ToolResults.GuardProject(_filesystem, projectPath, sw) is { } guardFailure) {
            return guardFailure;
        }
        if (string.IsNullOrWhiteSpace(query)) {
            return ToolResults.Failure("Query must not be empty.",
                [new ToolError(ToolErrorCodes.InvalidArgument,
                    "The 'query' parameter must not be empty.",
                    "Provide a non-empty search substring.")],
                sw);
        }

        if (ToolArgs.ParseChoice(mode, "mode", ["text", "symbol", "activity", "workflow"], sw, out var parsedMode) is { } modeError) {
            return modeError;
        }

        switch (parsedMode) {
            case "text": {
                    var result = await _search.SearchTextAsync(projectPath, query, cancellationToken);
                    var summary = $"Found {result.Matches.Count} text match(es) for '{query}' across {result.FilesSearched} file(s). Next: read_workflow_file.";
                    return ToolResults.Ok(summary, result, sw, result.Warnings);
                }
            case "symbol": {
                    var result = await _search.SearchSymbolsAsync(projectPath, query, kind, cancellationToken);
                    var summary = $"Found {result.Matches.Count} symbol(s) matching '{query}'. Next: get_code_context.";
                    return ToolResults.Ok(summary, result, sw, result.Warnings);
                }
            case "activity": {
                    var result = await _search.SearchActivitiesAsync(projectPath, query, cancellationToken);
                    var summary = $"Found {result.Matches.Count} activity match(es) for '{query}' across {result.WorkflowsSearched} workflow(s). Next: find_activity.";
                    return ToolResults.Ok(summary, result, sw, result.Warnings);
                }
            default: {
                    var result = await _search.SearchWorkflowsAsync(projectPath, query, cancellationToken);
                    var summary = $"Found {result.Matches.Count} workflow(s) matching '{query}'. Next: read_workflow_file.";
                    return ToolResults.Ok(summary, result, sw, result.Warnings);
                }
        }
    }
}
