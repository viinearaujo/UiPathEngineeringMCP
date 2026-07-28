using UiPath.Engineering.Mcp.Core.Models;

namespace UiPath.Engineering.Mcp.Tools.Tests;

public class AnalyzeProjectToolTests {
    [Fact]
    public async Task AnalyzeProject_WhenPathNotAllowed_ReturnsError() {
        var fs = new FakeFilesystemProvider { Allowed = false };
        var builder = new FakeProjectModelBuilder();
        var tool = new AnalyzeProjectTool(fs, builder);

        var result = await tool.AnalyzeProject("/not/allowed");

        Assert.Equal("error", result.Status);
        Assert.Equal("Path not allowed.", result.Summary);
        Assert.Null(result.Data);
    }

    [Fact]
    public async Task AnalyzeProject_HappyPath_ReturnsSuccessWithModel() {
        var fs = new FakeFilesystemProvider { Allowed = true };
        var model = new UiPathProjectModel { ProjectName = "testProcess", MainWorkflow = "Main.xaml" };
        var builder = new FakeProjectModelBuilder { Model = model };
        var tool = new AnalyzeProjectTool(fs, builder);

        var result = await tool.AnalyzeProject("/projects/testProcess");

        Assert.Equal("success", result.Status);
        Assert.Equal("Project analyzed successfully.", result.Summary);
        Assert.Same(model, result.Data);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task AnalyzeProject_WhenProjectJsonMissing_ReturnsStructuredError() {
        var fs = new FakeFilesystemProvider { Allowed = true };
        var builder = new FakeProjectModelBuilder {
            ToThrow = new FileNotFoundException("project.json not found.")
        };
        var tool = new AnalyzeProjectTool(fs, builder);

        var result = await tool.AnalyzeProject("/projects/empty");

        Assert.Equal("error", result.Status);
        Assert.Equal("project.json not found.", result.Summary);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public async Task AnalyzeProject_WhenUnexpectedError_DoesNotThrowAndReturnsError() {
        var fs = new FakeFilesystemProvider { Allowed = true };
        var builder = new FakeProjectModelBuilder {
            ToThrow = new InvalidOperationException("boom")
        };
        var tool = new AnalyzeProjectTool(fs, builder);

        var result = await tool.AnalyzeProject("/projects/testProcess");

        Assert.Equal("error", result.Status);
        Assert.Equal("Project analysis failed.", result.Summary);
        Assert.Contains("boom", result.Errors);
    }
}
