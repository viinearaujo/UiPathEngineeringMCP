using UiPath.Engineering.Mcp.Core.Docs;

namespace UiPath.Engineering.Mcp.Tools.Tests;

public class PatchProjectJsonToolTests {
    private const string ProjectPath = "/projects/testProcess";

    private static FakeFilesystemProvider FsWithProjectJson() {
        var fs = new FakeFilesystemProvider();
        fs.ProjectJsonContent = """
            {
              "name": "testProcess",
              "expressionLanguage": "CSharp",
              "entryPoints": [],
              "dependencies": {},
              "runtimeOptions": {}
            }
            """;
        return fs;
    }

    [Fact]
    public void ImmutableKey_IsRefused() {
        var fs = FsWithProjectJson();
        var result = new PatchProjectJsonTool(fs).PatchProjectJson(ProjectPath, ProjectJsonPatcher.SetRuntimeOption, key: "expressionLanguage", value: "\"VisualBasic\"");

        Assert.Equal("error", result.Status);
        Assert.Contains("expressionLanguage", result.Summary);
        Assert.Empty(fs.Writes);
    }

    [Fact]
    public void AddEntryPoint_WritesProjectJson() {
        var fs = FsWithProjectJson();
        var result = new PatchProjectJsonTool(fs).PatchProjectJson(ProjectPath, ProjectJsonPatcher.AddEntryPoint, filePath: "Worker.cs");

        Assert.Equal("success", result.Status);
        Assert.Contains("Worker.cs", fs.Writes[fs.ProjectJson!]);
    }
}
