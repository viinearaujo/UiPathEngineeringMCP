using UiPath.Engineering.Mcp.Core.Configuration;

namespace UiPath.Engineering.Mcp.Tools.Tests;

public class ImplementUiPathGoalPromptTests {
    [Fact]
    public void Render_IsThinRecipeOfCopilotLoop() {
        var text = ImplementUiPathGoalPrompt.Render(
            @"C:/Users/arauj/Documents/uipath/perf",
            "Finish dispatcher retries");

        Assert.Contains("C:/Users/arauj/Documents/uipath/perf", text);
        Assert.Contains("Finish dispatcher retries", text);
        Assert.Contains("copilot-studio-agent-instructions.txt", text);
        Assert.Contains(CopilotConnectorTools.JoinDefaultNames(), text);
        Assert.Contains("analyze_project", text);
        Assert.Contains("detail=summary", text);
        Assert.Contains("add_coded_workflow", text);
        Assert.Contains("relativeFolder", text);
        Assert.Contains("edit_workflow_file", text);
        Assert.Contains("get_compile_errors", text);
        Assert.DoesNotContain("recommend_activities", text);
        Assert.Contains("validate_project", text);
        Assert.Contains("build:false", text);
        Assert.Contains("pack:false", text);
        Assert.Contains("update_plan_task", text);
        Assert.Contains("docs/implementation-plan.json", text);
        Assert.Contains("get_implementation_plan", text);
        Assert.Contains("Do not call verify_work", text);
        Assert.Contains("not blocked on docs/ADR freshness", text);
        Assert.DoesNotContain("analyze_project_gaps", text);
        Assert.DoesNotContain("manage_project_docs", text);
        Assert.DoesNotContain("sync_project_context", text);
        Assert.DoesNotContain("done requires current docs", text);
    }
}
