using UiPath.Engineering.Mcp.Providers.UiPathCli;

namespace UiPath.Engineering.Mcp.Tools.Tests;

public class ValidateProjectToolTests
{
    [Fact]
    public async Task ValidateProject_WhenPathNotAllowed_ReturnsError()
    {
        var fs = new FakeFilesystemProvider { Allowed = false };
        var cli = new FakeUiPathCliProvider();
        var tool = new ValidateProjectTool(cli, fs);

        var result = await tool.ValidateProject("/not/allowed");

        Assert.Equal("error", result.Status);
        Assert.Equal("Path not allowed.", result.Summary);
    }

    [Fact]
    public async Task ValidateProject_WhenProjectJsonMissing_ReturnsError()
    {
        var fs = new FakeFilesystemProvider { Allowed = true, ProjectJson = null };
        var cli = new FakeUiPathCliProvider();
        var tool = new ValidateProjectTool(cli, fs);

        var result = await tool.ValidateProject("/projects/empty");

        Assert.Equal("error", result.Status);
        Assert.Equal("project.json not found.", result.Summary);
    }

    [Fact]
    public async Task ValidateProject_WhenCliSucceeds_ReturnsSuccess()
    {
        var fs = new FakeFilesystemProvider { Allowed = true };
        var cli = new FakeUiPathCliProvider
        {
            Result = new UiPathCliResult { Success = true, Summary = "Validation completed." }
        };
        var tool = new ValidateProjectTool(cli, fs);

        var result = await tool.ValidateProject("/projects/testProcess");

        Assert.Equal("success", result.Status);
        Assert.Equal("Validation completed.", result.Summary);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task ValidateProject_WhenCliFails_PropagatesErrorsAndWarnings()
    {
        var fs = new FakeFilesystemProvider { Allowed = true };
        var cli = new FakeUiPathCliProvider
        {
            Result = new UiPathCliResult
            {
                Success = false,
                Summary = "Validation failed.",
                Errors = ["[restore] boom"],
                Warnings = ["[analyze] heads up"]
            }
        };
        var tool = new ValidateProjectTool(cli, fs);

        var result = await tool.ValidateProject("/projects/testProcess");

        Assert.Equal("error", result.Status);
        Assert.Contains("[restore] boom", result.Errors);
        Assert.Contains("[analyze] heads up", result.Warnings);
    }
}
