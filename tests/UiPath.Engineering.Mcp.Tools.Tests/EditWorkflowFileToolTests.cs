using System.Text.Json;

namespace UiPath.Engineering.Mcp.Tools.Tests;

public class EditWorkflowFileToolTests {
    private const string ProjectPath = "/projects/testProcess";

    private static string Target(string relative) =>
        Path.Combine(Path.GetFullPath(ProjectPath), relative.Replace('/', Path.DirectorySeparatorChar));

    [Fact]
    public void EditWorkflowFile_SingleMatch_Replaces() {
        var fs = new FakeFilesystemProvider();
        fs.FileContents[Target("Main.cs")] = "var a = 1;\nvar b = 2;";
        var tool = new EditWorkflowFileTool(fs);

        var result = tool.EditWorkflowFile(ProjectPath, "Main.cs", "var b = 2;", "var b = 3;");
        var data = JsonSerializer.SerializeToElement(result.Data);

        Assert.Equal("success", result.Status);
        Assert.Equal(1, data.GetProperty("replacements").GetInt32());
        Assert.Equal("var a = 1;\nvar b = 3;", fs.Writes[Target("Main.cs")]);
    }

    [Fact]
    public void EditWorkflowFile_ZeroMatches_ReturnsError() {
        var fs = new FakeFilesystemProvider();
        fs.FileContents[Target("Main.cs")] = "var a = 1;";
        var tool = new EditWorkflowFileTool(fs);

        var result = tool.EditWorkflowFile(ProjectPath, "Main.cs", "missing", "x");

        Assert.Equal("error", result.Status);
        Assert.Empty(fs.Writes);
    }

    [Fact]
    public void EditWorkflowFile_MultipleMatches_RequiresReplaceAll() {
        var fs = new FakeFilesystemProvider();
        fs.FileContents[Target("Main.cs")] = "foo\nfoo";
        var tool = new EditWorkflowFileTool(fs);

        var result = tool.EditWorkflowFile(ProjectPath, "Main.cs", "foo", "bar");

        Assert.Equal("error", result.Status);
        Assert.Contains("replaceAll", result.Errors[0]);
        Assert.Empty(fs.Writes);
    }

    [Fact]
    public void EditWorkflowFile_ReplaceAll_ReplacesEveryMatch() {
        var fs = new FakeFilesystemProvider();
        fs.FileContents[Target("Main.cs")] = "foo\nfoo\nfoo";
        var tool = new EditWorkflowFileTool(fs);

        var result = tool.EditWorkflowFile(ProjectPath, "Main.cs", "foo", "bar", replaceAll: true);
        var data = JsonSerializer.SerializeToElement(result.Data);

        Assert.Equal("success", result.Status);
        Assert.Equal(3, data.GetProperty("replacements").GetInt32());
        Assert.Equal("bar\nbar\nbar", fs.Writes[Target("Main.cs")]);
    }

    [Fact]
    public void EditWorkflowFile_RejectsDisallowedExtension() {
        var fs = new FakeFilesystemProvider();
        fs.FileContents[Target("Data/Config.json")] = "{}";
        var tool = new EditWorkflowFileTool(fs);

        var result = tool.EditWorkflowFile(ProjectPath, "Data/Config.json", "{}", "{ }");

        Assert.Equal("error", result.Status);
        Assert.Empty(fs.Writes);
    }

    [Fact]
    public void EditWorkflowFile_MissingFile_ReturnsError() {
        var fs = new FakeFilesystemProvider();
        var tool = new EditWorkflowFileTool(fs);

        var result = tool.EditWorkflowFile(ProjectPath, "Nope.cs", "a", "b");

        Assert.Equal("error", result.Status);
    }

    [Fact]
    public void EditWorkflowFile_RejectsPathOutsideProject() {
        var fs = new FakeFilesystemProvider();
        var tool = new EditWorkflowFileTool(fs);

        var result = tool.EditWorkflowFile(ProjectPath, "../../evil.cs", "a", "b");

        Assert.Equal("error", result.Status);
    }
}
