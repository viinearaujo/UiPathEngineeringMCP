using UiPath.Engineering.Mcp.Core.Configuration;

namespace UiPath.Engineering.Mcp.Server.Tests;

public class CopilotConnectorDocumentationTests {
    [Fact]
    public void ReadmeAndCopilotInstructions_DocumentTheDefaultConnector() {
        var readme = File.ReadAllText(ResolveDoc("README.md"));
        var instructions = File.ReadAllText(ResolveDoc(Path.Combine("docs", "copilot-studio-agent-instructions.txt")));

        var recommendedLine = "**Recommended tools (default connector, ≤12):** "
            + CopilotConnectorTools.JoinDefaultNamesMarkdown();
        Assert.Contains(recommendedLine, readme);

        Assert.Contains(CopilotConnectorTools.JoinDefaultNames(), SplitLines(instructions));

        foreach (var name in CopilotConnectorTools.DefaultNames) {
            Assert.Contains(name, readme);
            Assert.Contains(name, instructions);
        }

        Assert.Contains("McpServer:HttpAuth", readme);
        Assert.Contains("not blocked on docs", instructions);
        Assert.Contains("write_workflow_file", readme);
        Assert.Contains("ToolSurface=All", readme);
    }

    [Fact]
    public void ImplementUiPathGoalPrompt_ListsDefaultNamesFromCanonicalArray() {
        var text = UiPath.Engineering.Mcp.Tools.ImplementUiPathGoalPrompt.Render(
            @"C:/Users/arauj/Documents/uipath/perf",
            "Finish dispatcher retries");

        Assert.Contains(CopilotConnectorTools.JoinDefaultNames(), text);
    }

    private static IReadOnlyList<string> SplitLines(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToArray();

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
