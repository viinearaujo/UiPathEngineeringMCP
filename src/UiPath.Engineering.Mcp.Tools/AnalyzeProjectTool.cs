using System.ComponentModel;
using System.Diagnostics;
using ModelContextProtocol.Server;
using UiPath.Engineering.Mcp.Core.Abstractions;
using UiPath.Engineering.Mcp.Core.Models;
using UiPath.Engineering.Mcp.Core.Parsing;

namespace UiPath.Engineering.Mcp.Tools;

[McpServerToolType]
public sealed class AnalyzeProjectTool {
    private readonly IFilesystemProvider _filesystem;
    private readonly IProjectModelBuilder _modelBuilder;

    public AnalyzeProjectTool(IFilesystemProvider filesystem, IProjectModelBuilder modelBuilder) {
        _filesystem = filesystem;
        _modelBuilder = modelBuilder;
    }

    [McpServerTool(UseStructuredContent = true), Description("Analyzes a UiPath project and returns structured metadata. Default detail is 'summary' (counts + workflow index, no activity trees). Pass detail='full' to page complete workflow models; pass workflowFile to load one workflow fully.")]
    public async Task<ToolResult> AnalyzeProject(
        [Description("Absolute path to the UiPath project directory.")] string projectPath,
        [Description("summary (default) or full.")] string detail = "summary",
        [Description("1-based page of workflows when detail=full.")] int page = 1,
        [Description("Workflows per page when detail=full (1-50, default 20).")] int pageSize = 20,
        [Description("Optional workflow file name to return that workflow's full model.")] string? workflowFile = null,
        CancellationToken cancellationToken = default) {
        var sw = Stopwatch.StartNew();

        if (ToolResults.GuardAllowedPath(_filesystem, projectPath, sw) is { } guardFailure) {
            return guardFailure;
        }

        try {
            var model = await _modelBuilder.BuildAsync(projectPath, cancellationToken);
            var view = ProjectAnalysisView.ToResult(model, detail, page, pageSize, workflowFile);
            return ToolResults.Ok("Project analyzed successfully.", view, sw, view.Warnings);
        } catch (ArgumentException ex) {
            return ToolResults.Failure(ex.Message, sw);
        } catch (Exception ex) {
            // Never surface a raw exception/stack trace to the MCP client.
            return ToolResults.FromException(ex, "Project analysis failed.", sw);
        }
    }
}
