using System.ComponentModel;
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

    public UpdatePlanTaskTool(IFilesystemProvider filesystem, ImplementationPlanStore planStore) {
        _filesystem = filesystem;
        _planStore = planStore;
    }

    [McpServerTool(UseStructuredContent = true), Description("Updates the status (pending/in_progress/done/blocked) and optional notes of a single task in the project's implementation plan.")]
    public ToolResult UpdatePlanTask(
        [Description("Absolute path to the UiPath project directory (must contain project.json).")] string projectPath,
        [Description("ID of the task to update (e.g. 'task-1').")] string taskId,
        [Description("New status: pending, in_progress, done, or blocked.")] string status,
        [Description("Optional notes to attach to the task.")] string? notes = null) {

        var sw = Stopwatch.StartNew();

        if (ToolResults.GuardProject(_filesystem, projectPath, sw) is { } guardFailure) {
            return guardFailure;
        }

        if (status is not (PlanTask.Pending or PlanTask.InProgress or PlanTask.Done or PlanTask.Blocked)) {
            return ToolResults.Failure(
                $"Invalid status '{status}'.",
                $"Status must be one of: {PlanTask.Pending}, {PlanTask.InProgress}, {PlanTask.Done}, {PlanTask.Blocked}.",
                sw);
        }

        var plan = _planStore.Load(projectPath);
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

        task.Status = status;
        if (notes is not null) {
            task.Notes = notes;
        }

        _planStore.Save(projectPath, plan);

        return ToolResults.Ok($"Task '{task.Id}' updated to '{status}'.", task, sw);
    }
}
