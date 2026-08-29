namespace UiPath.Engineering.Mcp.Tools.Tests;

public class ImplementUiPathGoalPromptTests {
    [Fact]
    public void Render_IncludesSafeLoopAndForbidsVerifyWorkGate() {
        var text = ImplementUiPathGoalPrompt.Render(
            @"C:/Users/arauj/Documents/uipath/perf",
            "Finish dispatcher retries");

        Assert.Contains("C:/Users/arauj/Documents/uipath/perf", text);
        Assert.Contains("Finish dispatcher retries", text);
        Assert.Contains("analyze_project", text);
        Assert.Contains("detail=summary", text);
        Assert.Contains("validate_project", text);
        Assert.Contains("build:false", text);
        Assert.Contains("update_plan_task", text);
        Assert.Contains("docs/implementation-plan.json", text);
        Assert.Contains("get_implementation_plan", text);
        Assert.Contains("verify_work as the done gate", text);
        Assert.Contains("Do not call verify_work", text);
        Assert.Contains("create_implementation_plan only if none exists", text);
        Assert.Contains("never rewrite redacted", text);
    }
}
