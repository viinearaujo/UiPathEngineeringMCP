using System.ComponentModel;
using System.Diagnostics;
using ModelContextProtocol.Server;
using UiPath.Engineering.Mcp.Core.Abstractions;
using UiPath.Engineering.Mcp.Core.Models;
using UiPath.Engineering.Mcp.Core.Planning;

namespace UiPath.Engineering.Mcp.Tools;

public sealed class PlanTaskInput {
    [Description("Task title (required).")]
    public string Title { get; set; } = string.Empty;

    [Description("What the task involves.")]
    public string? Description { get; set; }

    [Description("Project-relative files this task is expected to create or modify.")]
    public List<string>? TargetFiles { get; set; }

    [Description("Conditions that must hold for the task to be considered done.")]
    public List<string>? AcceptanceCriteria { get; set; }
}

[McpServerToolType]
public sealed class CreateImplementationPlanTool {
    private readonly IFilesystemProvider _filesystem;
    private readonly ImplementationPlanStore _planStore;

    public CreateImplementationPlanTool(IFilesystemProvider filesystem, ImplementationPlanStore planStore) {
        _filesystem = filesystem;
        _planStore = planStore;
    }

    [McpServerTool, Description("Creates an implementation plan for a UiPath project from a goal and an ordered task list. Writes docs/implementation-plan.json plus a Markdown mirror inside the project. Refuses to overwrite an existing plan unless overwrite is true.")]
    public ToolResult CreateImplementationPlan(
        [Description("Absolute path to the UiPath project directory (must contain project.json).")] string projectPath,
        [Description("Overall goal of the implementation plan.")] string goal,
        [Description("Tasks to schedule, in execution order.")] List<PlanTaskInput> tasks,
        [Description("Replace an existing plan? Defaults to false.")] bool overwrite = false) {

        var sw = Stopwatch.StartNew();

        if (ToolResults.GuardProject(_filesystem, projectPath, sw) is { } guardFailure) {
            return guardFailure;
        }

        if (string.IsNullOrWhiteSpace(goal)) {
            return ToolResults.Failure("goal is required.", sw);
        }

        if (tasks is null || tasks.Count == 0) {
            return ToolResults.Failure("Provide at least one task.", sw);
        }

        if (tasks.Any(t => string.IsNullOrWhiteSpace(t.Title))) {
            return ToolResults.Failure("Every task requires a title.", sw);
        }

        if (_planStore.Exists(projectPath) && !overwrite) {
            return ToolResults.Failure(
                "An implementation plan already exists for this project.",
                "Pass overwrite: true to replace the existing plan.",
                sw);
        }

        var now = DateTimeOffset.UtcNow;
        var plan = new ImplementationPlan {
            Goal = goal,
            CreatedUtc = now,
            UpdatedUtc = now,
            Tasks = tasks.Select((t, i) => new PlanTask {
                Id = $"task-{i + 1}",
                Title = t.Title,
                Description = t.Description ?? string.Empty,
                Status = PlanTask.Pending,
                TargetFiles = t.TargetFiles ?? [],
                AcceptanceCriteria = t.AcceptanceCriteria ?? []
            }).ToList()
        };

        _planStore.Save(projectPath, plan);

        return ToolResults.Ok($"Implementation plan created with {plan.Tasks.Count} task(s).", plan, sw);
    }
}
