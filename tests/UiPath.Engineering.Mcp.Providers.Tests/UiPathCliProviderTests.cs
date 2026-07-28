using Microsoft.Extensions.Options;
using UiPath.Engineering.Mcp.Core.Configuration;
using UiPath.Engineering.Mcp.Providers.UiPathCli;

namespace UiPath.Engineering.Mcp.Providers.Tests;

public class UiPathCliProviderTests {
    private static UiPathCliProvider CreateSut(string executablePath) {
        var options = Options.Create(new UiPathCliOptions {
            ExecutablePath = executablePath,
            DefaultTimeoutSeconds = 5
        });
        return new UiPathCliProvider(options);
    }

    [Fact]
    public async Task ValidateAsync_NoStepsRequested_SucceedsWithoutRunningAnything() {
        var sut = CreateSut("uip.exe");

        var result = await sut.ValidateAsync("/some/project", validate: false, build: false, pack: false);

        Assert.True(result.Success);
        Assert.Equal("Validation completed.", result.Summary);
        Assert.Empty(result.Errors);
        Assert.Equal(string.Empty, result.Command);
        Assert.False(result.Validate.Executed);
        Assert.False(result.Build.Executed);
        Assert.False(result.Pack.Executed);
    }

    [Fact]
    public async Task RunAsync_CliNotOnPath_ReportsSearchedShimNamesAndInstallHint() {
        var sut = CreateSut("definitely-not-a-real-uip-xyz");

        var result = await sut.RunAsync("analyze", "\"/some/project\"");

        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Contains("definitely-not-a-real-uip-xyz.exe")
                                             && e.Contains("definitely-not-a-real-uip-xyz.cmd")
                                             && e.Contains("definitely-not-a-real-uip-xyz.ps1"));
        Assert.Contains(result.Errors, e => e.Contains("@uipath/cli"));
    }

    [Fact]
    public async Task ValidateAsync_MissingExecutable_ReturnsStructuredError() {
        // A path that cannot be started -> provider must return a structured error, not throw.
        var sut = CreateSut(Path.Combine(Path.GetTempPath(), "definitely-not-a-real-uip-xyz.exe"));

        var result = await sut.ValidateAsync("/some/project", validate: true, build: true, pack: false);

        Assert.False(result.Success);
        Assert.NotEmpty(result.Errors);
        Assert.Contains(result.Errors, e => e.Contains("UiPath CLI", StringComparison.OrdinalIgnoreCase)
                                             || e.Contains("start", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ValidateAsync_FirstStepFails_LaterStepsAreMarkedNotExecuted() {
        // Validate fails (missing executable) -> build must be skipped, not silently reported as clean.
        var sut = CreateSut(Path.Combine(Path.GetTempPath(), "definitely-not-a-real-uip-xyz.exe"));

        var result = await sut.ValidateAsync("/some/project", validate: true, build: true, pack: true);

        Assert.True(result.Validate.Executed);
        Assert.False(result.Validate.Success);
        Assert.NotEmpty(result.Validate.Errors);
        Assert.False(result.Build.Executed);
        Assert.False(result.Pack.Executed);
        Assert.Empty(result.Build.Errors);
        Assert.Empty(result.Pack.Errors);
    }

    [Fact]
    public async Task ValidateAsync_FirstStepUsesRpaValidateCommandLine() {
        var sut = CreateSut("definitely-not-a-real-uip-xyz");

        var result = await sut.ValidateAsync("/some/project", validate: true, build: true, pack: true);

        Assert.Contains("rpa validate --project-dir \"/some/project\" --output json", result.Command);
    }

    [Theory]
    [InlineData("validate", "rpa validate --project-dir \"C:\\projects\\testProcess\" --output json")]
    [InlineData("build", "rpa build \"C:\\projects\\testProcess\" --output json")]
    [InlineData("pack", "rpa pack \"C:\\projects\\testProcess\" --output json")]
    public void BuildVerbArguments_MapsStepsToRpaCommandLines(string verb, string expected) {
        Assert.Equal(expected, UiPathCliProvider.BuildVerbArguments(verb, @"C:\projects\testProcess"));
    }
}
