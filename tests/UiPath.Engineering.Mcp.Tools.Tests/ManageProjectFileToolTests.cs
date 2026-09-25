using UiPath.Engineering.Mcp.Core;
using UiPath.Engineering.Mcp.Core.Docs;

namespace UiPath.Engineering.Mcp.Tools.Tests;

public class ManageProjectFileToolTests {
    private const string ProjectPath = "/projects/testProcess";

    private static string Target(string relative) =>
        Path.Combine(Path.GetFullPath(ProjectPath), relative.Replace('/', Path.DirectorySeparatorChar));

    private static ManageProjectContentTool CreateTool(FakeFilesystemProvider? fs = null) {
        fs ??= new FakeFilesystemProvider();
        fs.ProjectJson ??= Path.Combine(Path.GetFullPath(ProjectPath), "project.json");
        var knowledge = DocsSupport.Knowledge(fs);
        var adrs = DocsSupport.Adrs(fs);
        return new ManageProjectContentTool(
            fs, knowledge, adrs, new ProjectDocsSearch(fs, knowledge, adrs),
            DocsSupport.Validator(fs), new FakeProjectModelBuilder(), DocsSupport.Renderer(fs));
    }

    [Fact]
    public async Task UnknownAction_ReturnsError() {
        var result = await CreateTool().ManageProjectContent(ProjectPath, "move", relativePath: "notes.md", content: "x");

        Assert.Equal("error", result.Status);
        Assert.Contains("action", result.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReservedPlanPath_IsRejected() {
        var result = await CreateTool().ManageProjectContent(ProjectPath, ManageProjectContentTool.WriteFile, relativePath: "docs/implementation-plan.json", content: "{}");

        Assert.Equal("error", result.Status);
        Assert.Contains("owned by another tool", result.Summary);
    }

    [Fact]
    public async Task SecretName_IsRejected() {
        var result = await CreateTool().ManageProjectContent(ProjectPath, ManageProjectContentTool.WriteFile, relativePath: ".env", content: "SECRET=1");

        Assert.Equal("error", result.Status);
    }

    [Fact]
    public async Task RedactedBody_IsRejected() {
        var result = await CreateTool().ManageProjectContent(ProjectPath, ManageProjectContentTool.WriteFile, relativePath: "notes.md", content: "token=***REDACTED***");

        Assert.Equal("error", result.Status);
        Assert.Contains("REDACTED", result.Summary);
    }

    [Fact]
    public async Task InvalidJson_IsRejected() {
        var result = await CreateTool().ManageProjectContent(ProjectPath, ManageProjectContentTool.WriteFile, relativePath: "settings.json", content: "{");

        Assert.Equal("error", result.Status);
        Assert.Contains("JSON", result.Summary);
    }

    [Fact]
    public async Task Write_HappyPath() {
        var fs = new FakeFilesystemProvider();
        var result = await CreateTool(fs).ManageProjectContent(ProjectPath, ManageProjectContentTool.WriteFile, relativePath: "docs/notes.md", content: "# hello");

        Assert.Equal("success", result.Status);
        Assert.Equal("# hello", fs.Writes[Target("docs/notes.md")]);
    }

    [Fact]
    public async Task Edit_RequiresSingleMatch() {
        var fs = new FakeFilesystemProvider();
        fs.FileContents[Target("docs/notes.md")] = "alpha\nalpha";
        var tool = CreateTool(fs);

        var zero = await tool.ManageProjectContent(ProjectPath, ManageProjectContentTool.EditFile, relativePath: "docs/notes.md", oldString: "missing", newString: "x");
        var ambiguous = await tool.ManageProjectContent(ProjectPath, ManageProjectContentTool.EditFile, relativePath: "docs/notes.md", oldString: "alpha", newString: "beta");
        fs.FileContents[Target("docs/notes.md")] = "alpha\n";
        var ok = await tool.ManageProjectContent(ProjectPath, ManageProjectContentTool.EditFile, relativePath: "docs/notes.md", oldString: "alpha", newString: "beta");

        Assert.Equal("error", zero.Status);
        Assert.Equal("error", ambiguous.Status);
        Assert.Equal("success", ok.Status);
    }

    [Fact]
    public async Task Delete_RemovesFile() {
        var fs = new FakeFilesystemProvider();
        fs.FileContents[Target("docs/notes.md")] = "x";

        var result = await CreateTool(fs).ManageProjectContent(ProjectPath, ManageProjectContentTool.DeleteFile, relativePath: "docs/notes.md");

        Assert.Equal("success", result.Status);
        Assert.Contains(Target("docs/notes.md"), fs.DeletedFiles);
        Assert.False(fs.FileExists(Target("docs/notes.md")));
    }
}
