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

        The server is passive. You own the loop. Trust UiPath facts from tools.

        Loop:
        1. analyze_project with detail=summary (add workflowFile or detail=full+page only when you need one workflow's activities).
        2. analyze_project_gaps — treat many orphan/unresolved hits as noise until you confirm with read_workflow_file.
        3. get_implementation_plan. Use create_implementation_plan only if none exists. Never overwrite a mature plan.
        4. Source of truth is docs/implementation-plan.json. Do not move it.
        5. Author one task. Prefer spec tools (validate_activity_spec → build_workflow / insert_activities / manage_workflow_data).
        6. Confirm writes with search_codebase or read_workflow_file (file truth). Never "fix" a redacted credential body; never rewrite redacted text to disk.
        7. validate_project with build:false and pack:false. That is the green gate.
        8. update_plan_task to done or blocked. Do not call verify_work as the done gate.
        9. You cannot edit project.json through MCP — leave that as a Studio punch-list.
        10. If a call times out or returns JSON-RPC -32603, retry once with different flags; do not send the same payload three times.

        Stop when the user interrupts, the project path is ambiguous, or a task stays blocked after a real validate_project failure.
        """;
}
