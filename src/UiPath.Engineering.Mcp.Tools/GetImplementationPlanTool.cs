using System.ComponentModel;
using System.Diagnostics;
using ModelContextProtocol.Server;
using UiPath.Engineering.Mcp.Core.Abstractions;
using UiPath.Engineering.Mcp.Core.Models;
using UiPath.Engineering.Mcp.Core.Planning;

namespace UiPath.Engineering.Mcp.Tools;

[McpServerToolType]
public sealed class GetImplementationPlanTool {
    private readonly IFilesystemProvider _filesystem;
    private readonly ImplementationPlanStore _planStore;

    public GetImplementationPlanTool(IFilesystemProvider filesystem, ImplementationPlanStore planStore) {
        _filesystem = filesystem;
        _planStore = planStore;
    }

    [McpServerTool(UseStructuredContent = true), Description("Returns the project's implementation plan with derived per-status task counts.")]
    public ToolResult GetImplementationPlan(
        [Description("Absolute path to the UiPath project directory (must contain project.json).")] string projectPath) {

        var sw = Stopwatch.StartNew();

        if (ToolResults.GuardProject(_filesystem, projectPath, sw) is { } guardFailure) {
            return guardFailure;
        }

        var plan = _planStore.Load(projectPath);
        if (plan is null) {
            return ToolResults.Failure(
                "No implementation plan found for this project.",
                "Create one first with create_implementation_plan.",
                sw);
        }

        return ToolResults.Ok(
            $"Implementation plan: {plan.Tasks.Count} task(s), {plan.Tasks.Count(t => t.Status == PlanTask.Done)} done.",
            new {
                plan,
                counts = new {
                    pending = plan.Tasks.Count(t => t.Status == PlanTask.Pending),
                    inProgress = plan.Tasks.Count(t => t.Status == PlanTask.InProgress),
                    done = plan.Tasks.Count(t => t.Status == PlanTask.Done),
                    blocked = plan.Tasks.Count(t => t.Status == PlanTask.Blocked)
                }
            }, sw);
    }
}
