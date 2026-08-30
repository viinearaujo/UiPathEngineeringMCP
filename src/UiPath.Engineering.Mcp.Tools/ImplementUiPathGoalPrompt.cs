using System.ComponentModel;
using ModelContextProtocol.Server;

namespace UiPath.Engineering.Mcp.Tools;

[McpServerPromptType]
public sealed class ImplementUiPathGoalPrompt {
    [McpServerPrompt(Name = "implement_uipath_goal"), Description("Recipe to implement a UiPath project goal using this MCP server's tools.")]
    public string ImplementUiPathGoal(
        [Description("Absolute path to the UiPath project (folder with project.json).")] string projectPath,
        [Description("The user goal to implement.")] string goal) =>
        Render(projectPath, goal);

    public static string Render(string projectPath, string goal) =>
        $"""
        You are implementing a UiPath project via the UiPath Engineering MCP tools.
        Project path: {projectPath}
        Goal: {goal}

        Follow the Copilot authoring loop. Source of truth: Copilot Studio agent instructions (docs/copilot-studio-agent-instructions.txt).

        Recipe:
        1. analyze_project with detail=summary.
        2. get_implementation_plan. Scratchpad is docs/implementation-plan.json. Continue without a plan if none exists (create_implementation_plan is not on the default connector).
        3. Author one task. Prefer recommend_activities, then validate_activity_spec → build_workflow / insert_activities / manage_workflow_data. Target inserts with activityId from find_activity. Never hand-write XAML.
        4. Confirm writes with search_codebase or read_workflow_file. Never rewrite redacted credential text to disk.
        5. validate_project with build:false and pack:false, then update_plan_task to done or blocked. Marking done is not blocked on docs/ADR freshness. Do not call verify_work as the done gate.

        Stop when the user interrupts, the project path is ambiguous, or a task stays blocked after a real validate_project failure.
        """;
}
