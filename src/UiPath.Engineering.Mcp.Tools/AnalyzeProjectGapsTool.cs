using System.ComponentModel;
using System.Diagnostics;
using ModelContextProtocol.Server;
using UiPath.Engineering.Mcp.Core.Abstractions;
using UiPath.Engineering.Mcp.Core.GapAnalysis;
using UiPath.Engineering.Mcp.Core.Models;
using UiPath.Engineering.Mcp.Core.Parsing;
using UiPath.Engineering.Mcp.Core.Planning;

namespace UiPath.Engineering.Mcp.Tools;

[McpServerToolType]
public sealed class AnalyzeProjectGapsTool {
    private readonly IFilesystemProvider _filesystem;
    private readonly IProjectModelBuilder _modelBuilder;
    private readonly ImplementationPlanStore _planStore;

    public AnalyzeProjectGapsTool(IFilesystemProvider filesystem, IProjectModelBuilder modelBuilder, ImplementationPlanStore planStore) {
        _filesystem = filesystem;
        _modelBuilder = modelBuilder;
        _planStore = planStore;
    }

    [McpServerTool, Description("Analyzes a UiPath project for deterministic hygiene gaps (missing entry point, orphan workflows, missing exception handling/logging/descriptions/tests, unresolved invocations), cross-checks the implementation plan, and names the MCP tool that fixes each gap.")]
    public async Task<ToolResult> AnalyzeProjectGaps(
        [Description("Absolute path to the UiPath project directory.")] string projectPath,
        CancellationToken cancellationToken = default) {

        var sw = Stopwatch.StartNew();

        if (ToolResults.GuardProject(_filesystem, projectPath, sw) is { } guardFailure) {
            return guardFailure;
        }

        try {
            var model = await _modelBuilder.BuildAsync(projectPath, cancellationToken);
            var plan = _planStore.Load(projectPath);
            var gaps = ProjectGapAnalyzer.Analyze(model, plan);

            return ToolResults.Ok($"{gaps.Count} gap(s) found.", new {
                gaps,
                counts = new {
                    error = gaps.Count(g => g.Severity == Gap.Error),
                    warning = gaps.Count(g => g.Severity == Gap.Warning),
                    info = gaps.Count(g => g.Severity == Gap.Info)
                },
                plan = new {
                    exists = plan is not null,
                    tasksDone = plan?.Tasks.Count(t => t.Status == PlanTask.Done) ?? 0,
                    tasksTotal = plan?.Tasks.Count ?? 0
                }
            }, sw);
        } catch (Exception ex) {
            // Never surface a raw exception/stack trace to the MCP client.
            return ToolResults.FromException(ex, "Gap analysis failed.", sw);
        }
    }
}
