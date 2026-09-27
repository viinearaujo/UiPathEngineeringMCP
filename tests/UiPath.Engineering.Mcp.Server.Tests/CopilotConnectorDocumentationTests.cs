using UiPath.Engineering.Mcp.Core.Configuration;

namespace UiPath.Engineering.Mcp.Server.Tests;

public class CopilotConnectorDocumentationTests {
    [Fact]
    public void ReadmeAndCopilotInstructions_DocumentTheDefaultConnector() {
        var readme = File.ReadAllText(ResolveDoc("README.md"));
        var instructions = File.ReadAllText(ResolveDoc(Path.Combine("docs", "copilot-studio-agent-instructions.txt")));

        var recommendedLine = "**Recommended tools (default connector):** "
            + CopilotConnectorTools.JoinDefaultNamesMarkdown();
        Assert.Contains(recommendedLine, readme);

        Assert.InRange(instructions.Length, 4500, 5000);
        Assert.DoesNotContain(CopilotConnectorTools.JoinDefaultNames(), instructions);

        foreach (var name in CopilotConnectorTools.DefaultNames) {
            Assert.Contains(name, readme);
        }

        Assert.Contains("CODED-FIRST", instructions);
        Assert.Contains("check_work", instructions);
        Assert.Contains("update_plan_task", instructions);
        Assert.Contains("WHEN TO ASK VS ACT", instructions);
        Assert.Contains("read_workflow_file", instructions);
        Assert.Contains("McpServer:HttpAuth", readme);
        Assert.Contains("not blocked on docs", instructions);
        Assert.Contains("write_workflow_file", readme);
        Assert.Contains("ToolSurface=All", readme);
        Assert.Contains("ReadOnly", readme);
        Assert.Contains(".canvas/snapshot.json", readme);
        Assert.Contains("canvasSnapshot", readme);
        Assert.Contains("`Execute` argument mismatches", readme);
    }

    [Fact]
    public void ImplementUiPathGoalPrompt_ListsDefaultNamesFromCanonicalArray() {
        var text = UiPath.Engineering.Mcp.Tools.ImplementUiPathGoalPrompt.Render(
            @"C:/Users/arauj/Documents/uipath/perf",
            "Finish dispatcher retries");

        Assert.Contains(CopilotConnectorTools.JoinDefaultNames(), text);
        Assert.Contains("If none exists, create_implementation_plan", text);
        Assert.DoesNotContain("not on the default connector", text);
    }

    private static string ResolveDoc(string relativePath) {
        var copied = Path.Combine(AppContext.BaseDirectory, Path.GetFileName(relativePath));
        if (File.Exists(copied)) {
            return copied;
        }

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null) {
            var candidate = Path.Combine(dir.FullName, relativePath);
            if (File.Exists(candidate)) {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException($"Could not locate '{relativePath}' from the test output directory.");
    }
}
