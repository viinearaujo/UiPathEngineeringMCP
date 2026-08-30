using System.Text.Json;
using UiPath.Engineering.Mcp.Core.Docs;

namespace UiPath.Engineering.Mcp.Tools.Tests;

public class ManageProjectDocsToolTests {
    private readonly string _projectPath = Path.Combine(Path.GetTempPath(), "mcp-docs-tool-" + Guid.NewGuid().ToString("N"));
    private readonly FakeFilesystemProvider _fs;

    public ManageProjectDocsToolTests() {
        _fs = new FakeFilesystemProvider { ProjectJson = Path.Combine(_projectPath, "project.json") };
    }

    private ManageProjectDocsTool CreateTool() {
        var knowledge = DocsSupport.Knowledge(_fs);
        var adrs = DocsSupport.Adrs(_fs);
        return new ManageProjectDocsTool(_fs, knowledge, adrs, new ProjectDocsSearch(_fs, knowledge, adrs), DocsSupport.Validator(_fs), new FakeProjectModelBuilder());
    }

    [Fact]
    public async Task UnknownAction_ReturnsError() {
        var result = await CreateTool().ManageProjectDocs(_projectPath, "archive");
        Assert.Equal("error", result.Status);
    }

    [Fact]
    public async Task WriteAdr_AutoNumbersAndSupersedes() {
        var tool = CreateTool();
        var first = await tool.ManageProjectDocs(_projectPath, "write", kind: "adr", title: "Use queues", content: ProjectAdrStore.RenderTemplate("Use queues", AdrRecord.Accepted, "c", "d", "e"), status: AdrRecord.Accepted);
        Assert.Equal("success", first.Status);
        var firstId = JsonSerializer.SerializeToElement(first.Data).GetProperty("Id").GetString();

        var second = await tool.ManageProjectDocs(_projectPath, "write", kind: "adr", title: "Use bus", content: ProjectAdrStore.RenderTemplate("Use bus", AdrRecord.Accepted, "c", "d", "e"), status: AdrRecord.Accepted, supersedes: firstId);
        Assert.Equal("success", second.Status);

        var listed = await tool.ManageProjectDocs(_projectPath, "list", kind: "adr");
        var adrs = JsonSerializer.SerializeToElement(listed.Data).GetProperty("adrs");
        Assert.Equal(2, adrs.GetArrayLength());
        Assert.Contains(adrs.EnumerateArray(), a => a.GetProperty("Status").GetString() == AdrRecord.Superseded);
    }

    [Fact]
    public async Task Search_KindFilter() {
        var tool = CreateTool();
        await tool.ManageProjectDocs(_projectPath, "write", kind: "memory", id: "retry-policy", title: "Retry", content: "Use queues for retry.");
        await tool.ManageProjectDocs(_projectPath, "write", kind: "adr", title: "Use queues", content: ProjectAdrStore.RenderTemplate("Use queues", AdrRecord.Accepted, "Need retry.", "Use queues.", "Ops cost."), status: AdrRecord.Accepted);

        var memory = await tool.ManageProjectDocs(_projectPath, "search", kind: "memory", query: "retry");
        var data = JsonSerializer.SerializeToElement(memory.Data);
        Assert.Equal("success", memory.Status);
        Assert.All(data.GetProperty("Matches").EnumerateArray(), m => Assert.Equal("memory", m.GetProperty("Kind").GetString()));
    }
}
