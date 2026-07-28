using System.Text.Json;
using UiPath.Engineering.Mcp.Providers.UiPathCli;

namespace UiPath.Engineering.Mcp.Tools.Tests;

public class ValidateProjectToolTests {
    [Fact]
    public async Task ValidateProject_WhenPathNotAllowed_ReturnsError() {
        var fs = new FakeFilesystemProvider { Allowed = false };
        var cli = new FakeUiPathCliProvider();
        var tool = new ValidateProjectTool(cli, fs);

        var result = await tool.ValidateProject("/not/allowed");

        Assert.Equal("error", result.Status);
        Assert.Equal("Path not allowed.", result.Summary);
    }

    [Fact]
    public async Task ValidateProject_WhenProjectJsonMissing_ReturnsError() {
        var fs = new FakeFilesystemProvider { Allowed = true, ProjectJson = null };
        var cli = new FakeUiPathCliProvider();
        var tool = new ValidateProjectTool(cli, fs);

        var result = await tool.ValidateProject("/projects/empty");

        Assert.Equal("error", result.Status);
        Assert.Equal("project.json not found.", result.Summary);
    }

    [Fact]
    public async Task ValidateProject_WhenCliSucceeds_ReturnsSuccess() {
        var fs = new FakeFilesystemProvider { Allowed = true };
        var cli = new FakeUiPathCliProvider {
            Result = new UiPathCliResult { Success = true, Summary = "Validation completed." }
        };
        var tool = new ValidateProjectTool(cli, fs);

        var result = await tool.ValidateProject("/projects/testProcess");

        Assert.Equal("success", result.Status);
        Assert.Equal("Validation completed.", result.Summary);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task ValidateProject_WhenCliFails_PropagatesErrorsAndWarnings() {
        var fs = new FakeFilesystemProvider { Allowed = true };
        var cli = new FakeUiPathCliProvider {
            Result = new UiPathCliResult {
                Success = false,
                Summary = "Validation failed.",
                Errors = ["[validate] boom"],
                Warnings = ["[build] heads up"]
            }
        };
        var tool = new ValidateProjectTool(cli, fs);

        var result = await tool.ValidateProject("/projects/testProcess");

        Assert.Equal("error", result.Status);
        Assert.Contains("[validate] boom", result.Errors);
        Assert.Contains("[build] heads up", result.Warnings);
    }

    private static JsonElement SerializeData(object? data) =>
        JsonSerializer.SerializeToElement(data);

    [Fact]
    public async Task ValidateProject_WhenCliSucceeds_DataHasPerStepShapeAndNoRecommendations() {
        var fs = new FakeFilesystemProvider { Allowed = true };
        var cli = new FakeUiPathCliProvider {
            Result = new UiPathCliResult {
                Success = true,
                Summary = "Validation completed.",
                Validate = new CliStepResult { Executed = true, Success = true },
                Build = new CliStepResult { Executed = true, Success = true, Warnings = ["[build] heads up"] }
            }
        };
        var tool = new ValidateProjectTool(cli, fs);

        var result = await tool.ValidateProject("/projects/testProcess");
        var data = SerializeData(result.Data);

        Assert.True(data.GetProperty("success").GetBoolean());
        Assert.True(data.GetProperty("validate").GetProperty("executed").GetBoolean());
        Assert.True(data.GetProperty("validate").GetProperty("success").GetBoolean());
        Assert.True(data.GetProperty("build").GetProperty("executed").GetBoolean());
        // pack was not executed -> distinguishable via executed:false, success:false.
        Assert.False(data.GetProperty("pack").GetProperty("executed").GetBoolean());
        Assert.False(data.GetProperty("pack").GetProperty("success").GetBoolean());
        Assert.Equal(0, data.GetProperty("recommendations").GetArrayLength());
    }

    [Fact]
    public async Task ValidateProject_WhenStepFails_DataMarksSkippedStepsAndRecommendsReview() {
        var fs = new FakeFilesystemProvider { Allowed = true };
        var cli = new FakeUiPathCliProvider {
            Result = new UiPathCliResult {
                Success = false,
                Summary = "Validation failed.",
                Validate = new CliStepResult { Executed = true, Success = false, Errors = ["[validate] boom"] },
                Errors = ["[validate] boom"]
            }
        };
        var tool = new ValidateProjectTool(cli, fs);

        var result = await tool.ValidateProject("/projects/testProcess");
        var data = SerializeData(result.Data);

        Assert.False(data.GetProperty("success").GetBoolean());
        Assert.True(data.GetProperty("validate").GetProperty("executed").GetBoolean());
        Assert.False(data.GetProperty("validate").GetProperty("success").GetBoolean());
        Assert.False(data.GetProperty("build").GetProperty("executed").GetBoolean());

        var recommendations = data.GetProperty("recommendations");
        Assert.Single(recommendations.EnumerateArray());
        Assert.Contains("validate", recommendations[0].GetString());
    }

    [Fact]
    public async Task ValidateProject_DefaultFlags_ValidateAndBuildOnly() {
        var fs = new FakeFilesystemProvider { Allowed = true };
        var cli = new FakeUiPathCliProvider();
        var tool = new ValidateProjectTool(cli, fs);

        await tool.ValidateProject("/projects/testProcess");

        Assert.Equal((true, true, false), cli.LastValidateFlags);
    }
}
