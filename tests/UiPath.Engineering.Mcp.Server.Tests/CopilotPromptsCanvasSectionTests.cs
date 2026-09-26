namespace UiPath.Engineering.Mcp.Server.Tests;

public class CopilotPromptsCanvasSectionTests {
    [Fact]
    public void Section5_IsTheCanvasSnapshotPromptPack() {
        var text = File.ReadAllText(ResolveDoc(Path.Combine("docs", "copilot-prompts.md")));
        Assert.Contains("## 1. New feature", text);
        Assert.Contains("## 4. Update project documentation", text);
        var section = text.IndexOf("## 5. Canvas snapshot", StringComparison.Ordinal);
        Assert.True(section > text.IndexOf("## 4. Update project documentation", StringComparison.Ordinal));
        var body = text[section..];
        Assert.Contains("`generate_documentation` is leave-off. Enable it before the skeleton template. `update_canvas_snapshot` is on the default connector. Do not read `.canvas/snapshot.json` into the chat. Do not edit that file with `manage_project_content`.", body);
        var skeleton = body.IndexOf("### 5.1 Skeleton", StringComparison.Ordinal);
        var overview = body.IndexOf("### 5.2 Overview", StringComparison.Ordinal);
        var next = body.IndexOf("### 5.3 Next workflows", StringComparison.Ordinal);
        Assert.True(skeleton > 0 && skeleton < overview && overview < next);
        Assert.Contains("format \"canvasSnapshot\"", body);
        Assert.Contains("Do not call explain_workflow. Do not call update_canvas_snapshot.", body);
        Assert.Contains("analyze_project (detail=summary)", body);
        Assert.Contains("at most 3 ids from nextNodeIds", body);
        Assert.Contains("Do not pass includeActivityTree.", body);
        Assert.Contains("Do not read .canvas/snapshot.json into the chat.", body);
        Assert.Contains("Report explainedCount / totalCount.", body);
    }

    private static string ResolveDoc(string relativePath) {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null) {
            var candidate = Path.Combine(dir.FullName, relativePath);
            if (File.Exists(candidate)) {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException(relativePath);
    }
}
