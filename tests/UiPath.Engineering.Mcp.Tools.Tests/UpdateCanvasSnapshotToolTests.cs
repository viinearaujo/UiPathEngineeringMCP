using System.Text.Json;
using UiPath.Engineering.Mcp.Core.Canvas;

namespace UiPath.Engineering.Mcp.Tools.Tests;

public class UpdateCanvasSnapshotToolTests {
    private const string Project = "/projects/testProcess";

    [Fact]
    public async Task Status_MissingFile_WritesNothing() {
        var fs = new FakeFilesystemProvider { Allowed = true };
        var tool = new UpdateCanvasSnapshotTool(fs);

        var result = await tool.UpdateCanvasSnapshot(Project);

        Assert.Equal("error", result.Status);
        Assert.Contains("snapshot.json does not exist", result.Summary);
        Assert.Empty(fs.Writes);
        Assert.Empty(fs.CreatedDirectories);
    }

    [Fact]
    public async Task Status_ReturnsNextNodeIds_AndDoesNotWrite() {
        var fs = Seed("""
            {
              "schemaVersion": 1,
              "project": { "entryPoint": "A.xaml", "overview": "Ready" },
              "nodes": [
                { "id": "A.xaml", "explanation": "done" },
                { "id": "B.xaml", "explanation": "" },
                { "id": "C.xaml", "explanation": "" },
                { "id": "D.xaml", "explanation": "" },
                { "id": "E.xaml", "explanation": "" }
              ],
              "edges": [
                { "sourceWorkflow": "A.xaml", "targetWorkflow": "C.xaml", "isResolved": true },
                { "sourceWorkflow": "A.xaml", "targetWorkflow": "B.xaml", "isResolved": true }
              ]
            }
            """);
        var before = fs.FileContents[CanvasSnapshotPath.ForProject(Project)];
        var tool = new UpdateCanvasSnapshotTool(fs);

        var result = await tool.UpdateCanvasSnapshot(Project, nodes: JsonDocument.Parse("[]").RootElement.Clone());

        Assert.Equal("success", result.Status);
        Assert.Equal(before, fs.FileContents[CanvasSnapshotPath.ForProject(Project)]);
        var json = JsonSerializer.Serialize(result.Data);
        Assert.Contains("\"written\":false", json);
        Assert.Contains("\"overviewWritten\":true", json);
        Assert.Contains("\"explainedCount\":1", json);
        Assert.Contains("\"totalCount\":5", json);
        Assert.Contains("\"remainingCount\":4", json);
        Assert.Contains("\"nextNodeIds\":[\"B.xaml\",\"C.xaml\",\"D.xaml\"]", json);
    }

    private static FakeFilesystemProvider Seed(string json) {
        var fs = new FakeFilesystemProvider { Allowed = true };
        fs.FileContents[CanvasSnapshotPath.ForProject(Project)] = json;
        return fs;
    }
}
