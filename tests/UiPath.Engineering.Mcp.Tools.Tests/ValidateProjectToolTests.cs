using System.Text.Json;
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

    private static JsonElement SerializeData(object? data) =>
        JsonSerializer.SerializeToElement(data);

    [Fact]
    public async Task ValidateProject_WhenCliSucceeds_DataHasPerStepShapeAndNoRecommendations()
    {
        var fs = new FakeFilesystemProvider { Allowed = true };
        var cli = new FakeUiPathCliProvider
        {
            Result = new UiPathCliResult
            {
                Success = true,
                Summary = "Validation completed.",
                Restore = new CliStepResult { Executed = true, Success = true },
                Analyze = new CliStepResult { Executed = true, Success = true, Warnings = ["[analyze] heads up"] }
            }
        };
        var tool = new ValidateProjectTool(cli, fs);

        var result = await tool.ValidateProject("/projects/testProcess");
        var data = SerializeData(result.Data);

        Assert.True(data.GetProperty("success").GetBoolean());
        Assert.True(data.GetProperty("restore").GetProperty("executed").GetBoolean());
        Assert.True(data.GetProperty("restore").GetProperty("success").GetBoolean());
        Assert.True(data.GetProperty("analyze").GetProperty("executed").GetBoolean());
        // pack was not executed -> distinguishable via executed:false, success:false.
        Assert.False(data.GetProperty("pack").GetProperty("executed").GetBoolean());
        Assert.False(data.GetProperty("pack").GetProperty("success").GetBoolean());
        Assert.Equal(0, data.GetProperty("recommendations").GetArrayLength());
    }

    [Fact]
    public async Task ValidateProject_WhenStepFails_DataMarksSkippedStepsAndRecommendsReview()
    {
        var fs = new FakeFilesystemProvider { Allowed = true };
        var cli = new FakeUiPathCliProvider
        {
            Result = new UiPathCliResult
            {
                Success = false,
                Summary = "Validation failed.",
                Restore = new CliStepResult { Executed = true, Success = false, Errors = ["[restore] boom"] },
                Errors = ["[restore] boom"]
            }
        };
        var tool = new ValidateProjectTool(cli, fs);

        var result = await tool.ValidateProject("/projects/testProcess");
        var data = SerializeData(result.Data);

        Assert.False(data.GetProperty("success").GetBoolean());
        Assert.True(data.GetProperty("restore").GetProperty("executed").GetBoolean());
        Assert.False(data.GetProperty("restore").GetProperty("success").GetBoolean());
        Assert.False(data.GetProperty("analyze").GetProperty("executed").GetBoolean());

        var recommendations = data.GetProperty("recommendations");
        Assert.Single(recommendations.EnumerateArray());
        Assert.Contains("restore", recommendations[0].GetString());
    }
}
