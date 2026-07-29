using System.Text.Json;

namespace UiPath.Engineering.Mcp.Tools.Tests;

public class ReadWorkflowFileToolTests {
    private const string ProjectPath = "/projects/testProcess";

    private static string Target(string relative) =>
        Path.Combine(Path.GetFullPath(ProjectPath), relative.Replace('/', Path.DirectorySeparatorChar));

    private static (FakeFilesystemProvider Fs, ReadWorkflowFileTool Tool) Create() {
        var fs = new FakeFilesystemProvider();
        return (fs, new ReadWorkflowFileTool(fs));
    }

    [Fact]
    public void ReadWorkflowFile_ReturnsLineNumberedContent() {
        var (fs, tool) = Create();
        fs.FileContents[Target("Main.cs")] = "line one\nline two";

        var result = tool.ReadWorkflowFile(ProjectPath, "Main.cs");
        var data = JsonSerializer.SerializeToElement(result.Data);

        Assert.Equal("success", result.Status);
        Assert.Equal("1\tline one\n2\tline two\n", data.GetProperty("content").GetString());
        Assert.Equal(2, data.GetProperty("totalLines").GetInt32());
        Assert.False(data.GetProperty("truncated").GetBoolean());
        Assert.Equal(0, data.GetProperty("redactedCount").GetInt32());
    }

    [Fact]
    public void ReadWorkflowFile_PaginatesWithStartLineAndLineCount() {
        var (fs, tool) = Create();
        fs.FileContents[Target("big.cs")] = string.Join("\n", Enumerable.Range(1, 10).Select(i => $"l{i}"));

        var result = tool.ReadWorkflowFile(ProjectPath, "big.cs", startLine: 4, lineCount: 2);
        var data = JsonSerializer.SerializeToElement(result.Data);

        Assert.Equal("success", result.Status);
        Assert.Equal("4\tl4\n5\tl5\n", data.GetProperty("content").GetString());
        Assert.True(data.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public void ReadWorkflowFile_RedactsSecrets() {
        var (fs, tool) = Create();
        fs.FileContents[Target("Data/Config.json")] =
            "{\n  \"LATAM_Password\": \"abc123\",\n  \"Proxy_Host\": \"mon-prod:9080\"\n}";

        var result = tool.ReadWorkflowFile(ProjectPath, "Data/Config.json");
        var data = JsonSerializer.SerializeToElement(result.Data);

        Assert.Equal("success", result.Status);
        Assert.Equal(1, data.GetProperty("redactedCount").GetInt32());
        Assert.DoesNotContain("abc123", data.GetProperty("content").GetString());
        Assert.Contains("mon-prod:9080", data.GetProperty("content").GetString());
    }

    [Fact]
    public void ReadWorkflowFile_RejectsEnvFile() {
        var (fs, tool) = Create();
        fs.FileContents[Target(".env")] = "X=1";

        var result = tool.ReadWorkflowFile(ProjectPath, ".env");

        Assert.Equal("error", result.Status);
    }

    [Fact]
    public void ReadWorkflowFile_RejectsBinaryContent() {
        var (fs, tool) = Create();
        fs.FileContents[Target("logo.png")] = "PNG\0binary";

        var result = tool.ReadWorkflowFile(ProjectPath, "logo.png");

        Assert.Equal("error", result.Status);
    }

    [Fact]
    public void ReadWorkflowFile_RejectsPathOutsideProject() {
        var (_, tool) = Create();

        var result = tool.ReadWorkflowFile(ProjectPath, "../../evil.cs");

        Assert.Equal("error", result.Status);
    }

    [Fact]
    public void ReadWorkflowFile_MissingFile_ReturnsError() {
        var (_, tool) = Create();

        var result = tool.ReadWorkflowFile(ProjectPath, "Nope.cs");

        Assert.Equal("error", result.Status);
    }
}
