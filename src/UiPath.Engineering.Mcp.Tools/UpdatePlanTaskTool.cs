using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using ModelContextProtocol.Server;
using UiPath.Engineering.Mcp.Core.Abstractions;
using UiPath.Engineering.Mcp.Core.Models;
using UiPath.Engineering.Mcp.Core.Planning;

namespace UiPath.Engineering.Mcp.Tools;

[McpServerToolType]
public sealed class UpdatePlanTaskTool {
    private readonly IFilesystemProvider _filesystem;
    private readonly ImplementationPlanStore _planStore;

    public UpdatePlanTaskTool(
        IFilesystemProvider filesystem,
        ImplementationPlanStore planStore) {
        _filesystem = filesystem;
        _planStore = planStore;
    }

    [McpServerTool(UseStructuredContent = true), Description("Updates the status (pending/in_progress/done/blocked) and optional notes of a single task in the project's implementation plan. The plan is a scratchpad; marking done is not blocked on docs or ADR freshness. Next: analyze_project_gaps.")]
    public async Task<ToolResult> UpdatePlanTask(
        [Description("Absolute path to the UiPath project directory (must contain project.json).")] string projectPath,
        [Description("ID of the task to update (e.g. 'task-1').")] string taskId,
        [Description("New status: pending, in_progress, done, or blocked.")]
        [AllowedValues(PlanTask.Pending, PlanTask.InProgress, PlanTask.Done, PlanTask.Blocked)] string status,
        [Description("Optional notes to attach to the task.")] string? notes = null,
        CancellationToken cancellationToken = default) {

        var sw = Stopwatch.StartNew();

        if (ToolResults.GuardProject(_filesystem, projectPath, sw) is { } guardFailure) {
            return guardFailure;
        }

        if (ToolArgs.ParseChoice(status, "status", [PlanTask.Pending, PlanTask.InProgress, PlanTask.Done, PlanTask.Blocked], sw, out var parsedStatus) is { } statusError) {
            return statusError;
        }

        if (ToolResults.LoadPlanOrFail(_planStore, projectPath, sw, out var plan) is { } planFailure) {
            return planFailure;
        }

        if (plan is null) {
            return ToolResults.Failure(
                "No implementation plan found for this project.",
                "Create one first with create_implementation_plan.",
                sw);
        }

        var task = plan.Tasks.FirstOrDefault(t => string.Equals(t.Id, taskId, StringComparison.OrdinalIgnoreCase));
        if (task is null) {
            return ToolResults.Failure($"Task '{taskId}' not found in the implementation plan.", sw);
        }

        task.Status = parsedStatus;
        if (notes is not null) {
            task.Notes = notes;
        }

        await _planStore.SaveAsync(projectPath, plan, cancellationToken);

        return ToolResults.Ok($"Task '{task.Id}' updated to '{parsedStatus}'.", task, sw);
    }
}
