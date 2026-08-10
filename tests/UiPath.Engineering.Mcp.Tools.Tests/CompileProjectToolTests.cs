namespace UiPath.Engineering.Mcp.Tools.Tests;

public class CompileProjectToolTests {
    private static FakeFilesystemProvider ProjectFilesystem() =>
        new() { Allowed = true, ProjectJson = "/projects/testProcess/project.json" };

    [Fact]
    public async Task CompileProject_PathNotAllowed_ReturnsError() {
        var tool = new CompileProjectTool(new FakeUiPathCliProvider(), new FakeFilesystemProvider { Allowed = false });

        var result = await tool.CompileProject("/not/allowed");

        Assert.Equal("error", result.Status);
        Assert.Equal("Path not allowed.", result.Summary);
    }

    [Fact]
    public async Task CompileProject_HappyPath_RunsBuildOnly() {
        var cli = new FakeUiPathCliProvider();
        var tool = new CompileProjectTool(cli, ProjectFilesystem());

        var result = await tool.CompileProject("/projects/testProcess");

        Assert.Equal((false, true, false), cli.LastValidateFlags);
        Assert.Equal("success", result.Status);
    }

    [Fact]
    public async Task CompileProject_CliFails_ReturnsErrorStatus() {
        var cli = new FakeUiPathCliProvider {
            Result = new() { Success = false, Summary = "Build failed.", Errors = ["error CS0103"] }
        };
        var tool = new CompileProjectTool(cli, ProjectFilesystem());

        var result = await tool.CompileProject("/projects/testProcess");

        Assert.Equal("error", result.Status);
        Assert.Contains("error CS0103", result.Errors);
    }
}
