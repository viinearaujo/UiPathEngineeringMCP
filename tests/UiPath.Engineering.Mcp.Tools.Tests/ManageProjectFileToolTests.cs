using System.Text.Json;
using UiPath.Engineering.Mcp.Core;

namespace UiPath.Engineering.Mcp.Tools.Tests;

public class ManageProjectFileToolTests {
    private const string ProjectPath = "/projects/testProcess";

    private static string Target(string relative) =>
        Path.Combine(Path.GetFullPath(ProjectPath), relative.Replace('/', Path.DirectorySeparatorChar));

    [Fact]
    public void UnknownAction_ReturnsError() {
        var result = new ManageProjectFileTool(new FakeFilesystemProvider()).ManageProjectFile(ProjectPath, "move", "notes.md", "x");

        Assert.Equal("error", result.Status);
        Assert.Contains("action must be", result.Summary);
    }

    [Fact]
    public void ReservedPlanPath_IsRejected() {
        var result = new ManageProjectFileTool(new FakeFilesystemProvider()).ManageProjectFile(ProjectPath, "write", "docs/implementation-plan.json", "{}");

        Assert.Equal("error", result.Status);
        Assert.Contains("owned by another tool", result.Summary);
    }

    [Fact]
    public void SecretName_IsRejected() {
        var result = new ManageProjectFileTool(new FakeFilesystemProvider()).ManageProjectFile(ProjectPath, "write", ".env", "SECRET=1");

        Assert.Equal("error", result.Status);
    }

    [Fact]
    public void RedactedBody_IsRejected() {
        var result = new ManageProjectFileTool(new FakeFilesystemProvider()).ManageProjectFile(ProjectPath, "write", "notes.md", "token=***REDACTED***");

        Assert.Equal("error", result.Status);
        Assert.Contains("REDACTED", result.Summary);
    }

    [Fact]
    public void InvalidJson_IsRejected() {
        var result = new ManageProjectFileTool(new FakeFilesystemProvider()).ManageProjectFile(ProjectPath, "write", "settings.json", "{");

        Assert.Equal("error", result.Status);
        Assert.Contains("JSON", result.Summary);
    }

    [Fact]
    public void Write_HappyPath() {
        var fs = new FakeFilesystemProvider();
        var result = new ManageProjectFileTool(fs).ManageProjectFile(ProjectPath, "write", "docs/notes.md", "# hello");

        Assert.Equal("success", result.Status);
        Assert.Equal("# hello", fs.Writes[Target("docs/notes.md")]);
    }

    [Fact]
    public void Edit_RequiresSingleMatch() {
        var fs = new FakeFilesystemProvider();
        fs.FileContents[Target("docs/notes.md")] = "alpha\nalpha";
        var tool = new ManageProjectFileTool(fs);

        var zero = tool.ManageProjectFile(ProjectPath, "edit", "docs/notes.md", oldString: "missing", newString: "x");
        var ambiguous = tool.ManageProjectFile(ProjectPath, "edit", "docs/notes.md", oldString: "alpha", newString: "beta");
        fs.FileContents[Target("docs/notes.md")] = "alpha\n";
        var ok = tool.ManageProjectFile(ProjectPath, "edit", "docs/notes.md", oldString: "alpha", newString: "beta");

        Assert.Equal("error", zero.Status);
        Assert.Equal("error", ambiguous.Status);
        Assert.Equal("success", ok.Status);
    }

    [Fact]
    public void Delete_RemovesFile() {
        var fs = new FakeFilesystemProvider();
        fs.FileContents[Target("docs/notes.md")] = "x";

        var result = new ManageProjectFileTool(fs).ManageProjectFile(ProjectPath, "delete", "docs/notes.md");

        Assert.Equal("success", result.Status);
        Assert.Contains(Target("docs/notes.md"), fs.DeletedFiles);
        Assert.False(fs.FileExists(Target("docs/notes.md")));
    }
}
