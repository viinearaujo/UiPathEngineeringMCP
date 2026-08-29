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
        var data = Assert.IsType<ProjectAnalysisResult>(result.Data);
        Assert.Equal("summary", data.Detail);
        Assert.Equal("testProcess", data.Summary.ProjectName);
        Assert.Null(data.Workflows);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task AnalyzeProject_Full_ReturnsPagedWorkflows() {
        var fs = new FakeFilesystemProvider { Allowed = true };
        var model = new UiPathProjectModel {
            ProjectName = "testProcess",
            Workflows = [
                new WorkflowModel { FileName = "Main.xaml" },
                new WorkflowModel { FileName = "Child.xaml" }
            ]
        };
        var tool = new AnalyzeProjectTool(fs, new FakeProjectModelBuilder { Model = model });

        var result = await tool.AnalyzeProject("/projects/testProcess", detail: "full", page: 1, pageSize: 1);

        var data = Assert.IsType<ProjectAnalysisResult>(result.Data);
        Assert.Equal("full", data.Detail);
        Assert.NotNull(data.Workflows);
        Assert.Single(data.Workflows);
        Assert.Equal("Main.xaml", data.Workflows[0].FileName);
        Assert.True(data.Truncated);
    }

    [Fact]
    public async Task AnalyzeProject_InvalidDetail_ReturnsError() {
        var fs = new FakeFilesystemProvider { Allowed = true };
        var tool = new AnalyzeProjectTool(fs, new FakeProjectModelBuilder());

        var result = await tool.AnalyzeProject("/projects/testProcess", detail: "tiny");

        Assert.Equal("error", result.Status);
        Assert.Contains("detail", result.Summary, StringComparison.OrdinalIgnoreCase);
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
