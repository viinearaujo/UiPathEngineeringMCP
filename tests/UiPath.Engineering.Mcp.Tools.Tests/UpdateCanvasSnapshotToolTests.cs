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

    [Fact]
    public async Task Patch_ChangesOnlyOverviewExplanationAndDecisions_AndRedactsProse() {
        var original = """
            {
              "schemaVersion": 1,
              "generatedAt": "2026-09-26T16:00:00Z",
              "generator": { "mcpVersion": "9.9.9" },
              "project": { "name": "Dispatch", "entryPoint": "A.xaml", "additionalEntryPoints": [], "overview": "" },
              "nodes": [
                {
                  "id": "A.xaml",
                  "kind": "xaml",
                  "sha256": "abc",
                  "studioId": "keep-me",
                  "arguments": [],
                  "explanation": "",
                  "decisions": []
                },
                { "id": "Coded.cs", "kind": "coded", "sha256": "def", "explanation": "", "decisions": [] }
              ],
              "edges": [
                {
                  "sourceWorkflow": "A.xaml",
                  "targetWorkflow": "Coded.cs",
                  "displayName": "Run",
                  "isResolved": true,
                  "argumentMappings": [
                    { "direction": "In", "targetArgument": "in_Secret", "expression": "password=***REDACTED***" }
                  ]
                }
              ]
            }
            """;
        var fs = Seed(original);
        var tool = new UpdateCanvasSnapshotTool(fs);
        using var overview = JsonDocument.Parse("\"token=abcd\"");
        using var nodes = JsonDocument.Parse("""
            [{
              "id": "A.xaml",
              "explanation": "  uses token=abcd  ",
              "decisions": [" keep ", "   ", "token=zzzz"]
            }]
            """);

        var result = await tool.UpdateCanvasSnapshot(
            Project,
            overview.RootElement.Clone(),
            nodes.RootElement.Clone());

        Assert.Equal("success", result.Status);
        var stored = fs.FileContents[CanvasSnapshotPath.ForProject(Project)];
        using var doc = JsonDocument.Parse(stored);
        var root = doc.RootElement;
        Assert.Equal("token=***REDACTED***", root.GetProperty("project").GetProperty("overview").GetString());
        Assert.Equal("Dispatch", root.GetProperty("project").GetProperty("name").GetString());
        Assert.Equal("A.xaml", root.GetProperty("project").GetProperty("entryPoint").GetString());
        Assert.Equal("2026-09-26T16:00:00Z", root.GetProperty("generatedAt").GetString());
        var node = root.GetProperty("nodes")[0];
        Assert.Equal("uses token=***REDACTED***", node.GetProperty("explanation").GetString());
        Assert.Equal(
            new[] { "keep", "token=***REDACTED***" },
            node.GetProperty("decisions").EnumerateArray().Select(item => item.GetString()).ToArray());
        Assert.Equal("abc", node.GetProperty("sha256").GetString());
        Assert.Equal("keep-me", node.GetProperty("studioId").GetString());
        Assert.Equal("password=***REDACTED***", root.GetProperty("edges")[0].GetProperty("argumentMappings")[0].GetProperty("expression").GetString());
        Assert.Equal("", root.GetProperty("nodes")[1].GetProperty("explanation").GetString());
        var payload = JsonSerializer.Serialize(result.Data);
        Assert.Contains("\"written\":true", payload);
        Assert.Contains("\"explainedCount\":1", payload);
    }

    [Theory]
    [InlineData("{\"schemaVersion\":1}", "invalid JSON")]
    [InlineData("{\"schemaVersion\":2,\"nodes\":[]}", "schemaVersion")]
    [InlineData("{\"schemaVersion\":1,\"nodes\":[{\"id\":\"A.xaml\"},{\"id\":\"A.xaml\"}]}", "duplicate id")]
    public async Task Patch_ReadErrors_LeaveBytesUnchanged(string json, string substring) {
        await AssertUnchanged(json, null, null, substring);
    }

    [Fact]
    public async Task Patch_UnknownId_LeavesBytesUnchanged() {
        await AssertUnchanged(TwoNodes(), null, """
            [{"id":"Missing.xaml","explanation":"hi","decisions":[]}]
            """, "unknown node id");
    }

    [Fact]
    public async Task Patch_BlankExplanation_LeavesBytesUnchanged() {
        await AssertUnchanged(TwoNodes(), null, """
            [{"id":"A.xaml","explanation":"  ","decisions":[]}]
            """, "explanation is blank");
    }

    [Fact]
    public async Task Patch_MissingDecisions_LeavesBytesUnchanged() {
        await AssertUnchanged(TwoNodes(), null, """
            [{"id":"A.xaml","explanation":"hi"}]
            """, "decisions is required");
    }

    [Fact]
    public async Task Patch_NonStringOverview_LeavesBytesUnchanged() {
        await AssertUnchanged(TwoNodes(), "1", null, "overview must be a string");
    }

    [Fact]
    public async Task Patch_CodedDecisions_LeavesBytesUnchanged() {
        await AssertUnchanged(TwoNodes(), null, """
            [{"id":"Coded.cs","explanation":"hi","decisions":["cache"]}]
            """, "coded node decisions must be empty");
    }

    [Fact]
    public async Task Patch_SecondNodeInvalid_DoesNotApplyTheFirst() {
        var fs = Seed(TwoNodes());
        var before = fs.FileContents[CanvasSnapshotPath.ForProject(Project)];
        var tool = new UpdateCanvasSnapshotTool(fs);
        using var nodes = JsonDocument.Parse("""
            [
              {"id":"A.xaml","explanation":"first","decisions":[]},
              {"id":"Coded.cs","explanation":"  ","decisions":[]}
            ]
            """);

        var result = await tool.UpdateCanvasSnapshot(Project, nodes: nodes.RootElement.Clone());

        Assert.Equal("error", result.Status);
        Assert.Contains("explanation is blank", result.Summary);
        Assert.Equal(before, fs.FileContents[CanvasSnapshotPath.ForProject(Project)]);
    }

    private async Task AssertUnchanged(string json, string? overviewJson, string? nodesJson, string substring) {
        var fs = Seed(json);
        var before = fs.FileContents[CanvasSnapshotPath.ForProject(Project)];
        var tool = new UpdateCanvasSnapshotTool(fs);
        using var overviewDoc = overviewJson is null ? null : JsonDocument.Parse(overviewJson);
        using var nodesDoc = nodesJson is null ? null : JsonDocument.Parse(nodesJson);

        var result = await tool.UpdateCanvasSnapshot(
            Project,
            overviewDoc?.RootElement.Clone(),
            nodesDoc?.RootElement.Clone());

        Assert.Equal("error", result.Status);
        Assert.Contains(substring, result.Summary);
        Assert.Equal(before, fs.FileContents[CanvasSnapshotPath.ForProject(Project)]);
    }

    private static string TwoNodes() => """
        {
          "schemaVersion": 1,
          "project": { "entryPoint": "A.xaml", "overview": "" },
          "nodes": [
            { "id": "A.xaml", "kind": "xaml", "sha256": "abc", "explanation": "", "decisions": [] },
            { "id": "Coded.cs", "kind": "coded", "sha256": "def", "explanation": "", "decisions": [] }
          ],
          "edges": []
        }
        """;

    private static FakeFilesystemProvider Seed(string json) {
        var fs = new FakeFilesystemProvider { Allowed = true };
        fs.FileContents[CanvasSnapshotPath.ForProject(Project)] = json;
        return fs;
    }
}
