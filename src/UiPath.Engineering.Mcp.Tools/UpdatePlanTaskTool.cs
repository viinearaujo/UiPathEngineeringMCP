using System.ComponentModel;
using System.Diagnostics;
using ModelContextProtocol.Server;
using UiPath.Engineering.Mcp.Core.Abstractions;
using UiPath.Engineering.Mcp.Core.Docs;
using UiPath.Engineering.Mcp.Core.Models;
using UiPath.Engineering.Mcp.Core.Parsing;
using UiPath.Engineering.Mcp.Core.Planning;

namespace UiPath.Engineering.Mcp.Tools;

[McpServerToolType]
public sealed class UpdatePlanTaskTool {
    private readonly IFilesystemProvider _filesystem;
    private readonly ImplementationPlanStore _planStore;
    private readonly IProjectModelBuilder _modelBuilder;
    private readonly ProjectDocsValidator _docsValidator;

    public UpdatePlanTaskTool(
        IFilesystemProvider filesystem,
        ImplementationPlanStore planStore,
        IProjectModelBuilder modelBuilder,
        ProjectDocsValidator docsValidator) {
        _filesystem = filesystem;
        _planStore = planStore;
        _modelBuilder = modelBuilder;
        _docsValidator = docsValidator;
    }

    [McpServerTool(UseStructuredContent = true), Description("Updates the status (pending/in_progress/done/blocked) and optional notes of a single task in the project's implementation plan. Marking done is refused when validate_project_docs would report error findings.")]
    public async Task<ToolResult> UpdatePlanTask(
        [Description("Absolute path to the UiPath project directory (must contain project.json).")] string projectPath,
        [Description("ID of the task to update (e.g. 'task-1').")] string taskId,
        [Description("New status: pending, in_progress, done, or blocked.")] string status,
        [Description("Optional notes to attach to the task.")] string? notes = null,
        CancellationToken cancellationToken = default) {

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

        List<string>? docsWarnings = null;
        if (status == PlanTask.Done) {
            UiPathProjectModel model;
            try {
                model = await _modelBuilder.BuildAsync(projectPath, cancellationToken);
            } catch (Exception ex) {
                return ToolResults.FromException(ex, "Project analysis failed.", sw);
            }

            var findings = _docsValidator.Validate(projectPath, model);
            var errors = findings.Where(f => f.Severity == DocsFinding.Error).ToList();
            if (errors.Count > 0) {
                if (task.Status != PlanTask.InProgress) {
                    task.Status = PlanTask.InProgress;
                    _planStore.Save(projectPath, plan);
                }

                return ToolResults.Failure(
                    "Cannot mark the task done while project docs have error findings.",
                    errors.Select(DocsGate.ToToolError).ToList(),
                    sw);
            }

            docsWarnings = findings.Where(f => f.Severity == DocsFinding.Warning).Select(f => f.Message).ToList();
        }

        task.Status = status;
        if (notes is not null) {
            task.Notes = notes;
        }

        _planStore.Save(projectPath, plan);

        return ToolResults.Ok($"Task '{task.Id}' updated to '{status}'.", task, sw, docsWarnings);
    }
}
