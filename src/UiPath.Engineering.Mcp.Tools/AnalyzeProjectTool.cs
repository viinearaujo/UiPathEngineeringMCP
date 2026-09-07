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

    [McpServerTool(UseStructuredContent = true), Description("Analyzes a UiPath project and returns structured metadata. Default detail is 'summary' (counts + workflow index, coded-file kind workflow/test/source, no activity trees). Pass detail='full' to page complete workflow models; pass workflowFile to load one workflow fully. Next: get_implementation_plan.")]
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

        if (ToolArgs.ParseChoice(detail, "detail", [ProjectAnalysisView.DetailSummary, ProjectAnalysisView.DetailFull], sw, out var parsedDetail) is { } detailError) {
            return detailError;
        }

        var model = await _modelBuilder.BuildAsync(projectPath, cancellationToken);
        var view = ProjectAnalysisView.ToResult(model, parsedDetail, page, pageSize, workflowFile);
        return ToolResults.Ok("Project analyzed successfully. Next: get_implementation_plan.", view, sw, view.Warnings);
    }
}
