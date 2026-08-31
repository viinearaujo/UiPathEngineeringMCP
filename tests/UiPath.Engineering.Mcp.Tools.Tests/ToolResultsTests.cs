using System.Diagnostics;
using System.Text.Json;
using UiPath.Engineering.Mcp.Core;
using UiPath.Engineering.Mcp.Core.Models;

namespace UiPath.Engineering.Mcp.Tools.Tests;

public class ToolResultsTests
{
    private static readonly Stopwatch Sw = Stopwatch.StartNew();

    [Fact]
    public void Ok_ProducesSuccessEnvelope()
    {
        var result = ToolResults.Ok("done.", new { value = 1 }, Sw);

        Assert.Equal("success", result.Status);
        Assert.Equal("done.", result.Summary);
        Assert.Empty(result.Errors);
        Assert.NotNull(result.Data);
    }

    [Fact]
    public void Failure_StructuredError_SetsStatusAndDetails()
    {
        var sw = Stopwatch.StartNew();
        var error = new ToolError("SPEC_UNKNOWN_ACTIVITY", "Unknown activity 'FoeEach'.", "Did you mean 'ForEach'? Check ActivityCatalog.All for valid names.");
        var result = ToolResults.Failure(error, sw);
        Assert.Equal("error", result.Status);
        var detail = Assert.Single(result.ErrorDetails);
        Assert.Equal("SPEC_UNKNOWN_ACTIVITY", detail.ErrorCode);
        Assert.Contains(error.Message, result.Errors[0]); // plain list kept in sync for old clients
    }

    [Fact]
    public void Failure_MirrorsMessageIntoSummaryAndErrors()
    {
        var result = ToolResults.Failure("broken.", Sw);

        Assert.Equal("error", result.Status);
        Assert.Equal("broken.", result.Summary);
        Assert.Equal(["broken."], result.Errors);
    }

    [Fact]
    public void Failure_WithErrorList_CarriesAllErrors()
    {
        var result = ToolResults.Failure("two things broke.", new[] { "e1", "e2" }, Sw);

        Assert.Equal("error", result.Status);
        Assert.Equal("two things broke.", result.Summary);
        Assert.Equal(["e1", "e2"], result.Errors);
    }

    [Fact]
    public void GuardAllowedPath_WhenNotAllowed_ReturnsFailure()
    {
        var fs = new FakeFilesystemProvider { Allowed = false };

        var result = ToolResults.GuardAllowedPath(fs, "/x", Sw);

        Assert.NotNull(result);
        Assert.Equal("Path not allowed.", result.Summary);
        var error = Assert.Single(result.ErrorDetails);
        Assert.Equal(ToolErrorCodes.PathNotAllowed, error.ErrorCode);
        Assert.DoesNotContain("/x", string.Join(" ", result.Errors));
    }

    [Fact]
    public void GuardProject_WhenProjectJsonMissing_ReturnsFailure()
    {
        var fs = new FakeFilesystemProvider { ProjectJson = null };

        var result = ToolResults.GuardProject(fs, "/x", Sw);

        Assert.NotNull(result);
        Assert.Equal("project.json not found.", result.Summary);
        var error = Assert.Single(result.ErrorDetails);
        Assert.Equal(ToolErrorCodes.ProjectJsonNotFound, error.ErrorCode);
    }

    [Fact]
    public void GuardProject_WhenUsable_ReturnsNull()
    {
        var fs = new FakeFilesystemProvider();

        Assert.Null(ToolResults.GuardProject(fs, "/x", Sw));
    }

    [Fact]
    public void TryResolveWithinProject_AcceptsChildAndRejectsEscape()
    {
        Assert.True(ToolResults.TryResolveWithinProject("/projects/p", "Main.xaml", out _));
        Assert.False(ToolResults.TryResolveWithinProject("/projects/p", "../evil.xaml", out _));
    }

    [Fact]
    public void FromException_MapsKnownFailureModes()
    {
        var notFound = ToolResults.FromException(new FileNotFoundException("leaked-path-xyz"), "Failed.", Sw);
        var badJson = ToolResults.FromException(new JsonException("Unexpected token at line 4"), "Failed.", Sw);
        var other = ToolResults.FromException(new InvalidOperationException("boom"), "Failed.", Sw);

        Assert.Equal("project.json not found.", notFound.Summary);
        Assert.Equal(ToolErrorCodes.ProjectJsonNotFound, Assert.Single(notFound.ErrorDetails).ErrorCode);
        Assert.DoesNotContain("leaked-path-xyz", string.Join(" ", notFound.Errors));

        Assert.Equal("project.json could not be parsed.", badJson.Summary);
        Assert.Equal(ToolErrorCodes.ProjectJsonInvalid, Assert.Single(badJson.ErrorDetails).ErrorCode);
        Assert.DoesNotContain("Unexpected token", string.Join(" ", badJson.Errors));

        Assert.Equal("Failed.", other.Summary);
        Assert.Equal(ToolErrorCodes.OperationFailed, Assert.Single(other.ErrorDetails).ErrorCode);
        Assert.DoesNotContain("boom", string.Join(" ", other.Errors));
    }
}
