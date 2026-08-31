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

        Assert.Contains("rpa validate --project-dir /some/project --output json", result.Command);
    }

    [Theory]
    [InlineData("/some/project&calc")]
    [InlineData("/some/project|whoami")]
    [InlineData("/some/project%PATH%")]
    [InlineData("/some/project^calc")]
    public async Task ValidateAsync_ProjectPathWithShellMetachars_IsNotRejectedAsInjection(string projectPath) {
        var sut = CreateSut("definitely-not-a-real-uip-xyz");

        var result = await sut.ValidateAsync(projectPath, validate: true, build: true, pack: true);

        Assert.DoesNotContain(result.Errors, e => e.Contains("metacharacters", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(result.Errors, e => e.Contains("control characters", StringComparison.OrdinalIgnoreCase));
        Assert.True(result.Validate.Executed);
        Assert.Contains(projectPath, result.Command);
        Assert.False(result.Build.Executed);
        Assert.False(result.Pack.Executed);
    }

    [Fact]
    public async Task ValidateAsync_ProjectPathWithNewline_RejectedWithoutExecuting() {
        var sut = CreateSut("definitely-not-a-real-uip-xyz");

        var result = await sut.ValidateAsync("/some/project\nwhoami", validate: true, build: true, pack: true);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Contains("control characters", StringComparison.OrdinalIgnoreCase));
        Assert.False(result.Validate.Executed);
        Assert.False(result.Build.Executed);
        Assert.False(result.Pack.Executed);
        Assert.Equal(string.Empty, result.Command);
    }

    [Fact]
    public async Task RunAsync_ArgumentsWithShellMetachars_AreTokenizedNotRejected() {
        var sut = CreateSut("definitely-not-a-real-uip-xyz");

        var result = await sut.RunAsync("rpa", "rpa validate --project-dir \"/some/project\" & calc");

        Assert.DoesNotContain(result.Errors, e => e.Contains("metacharacters", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(result.Errors, e => e.Contains("control characters", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("&", result.Command);
        Assert.Contains("calc", result.Command);
    }

    [Fact]
    public async Task RunAsync_ArgumentsWithNewline_RejectedWithoutExecuting() {
        var sut = CreateSut("definitely-not-a-real-uip-xyz");

        var result = await sut.RunAsync("rpa", "rpa validate --project-dir \"/some/project\"\nwhoami");

        Assert.False(result.Success);
        Assert.Equal(-1, result.ExitCode);
        Assert.Contains(result.Errors, e => e.Contains("control characters", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildVerbArguments_MapsStepsToRpaTokens() {
        var path = @"C:\projects\testProcess";

        Assert.Equal(
            ["rpa", "validate", "--project-dir", path, "--output", "json"],
            UiPathCliProvider.BuildVerbArguments("validate", path));
        Assert.Equal(
            ["rpa", "build", path, "--output", "json"],
            UiPathCliProvider.BuildVerbArguments("build", path));
        Assert.Equal(
            ["rpa", "pack", path, "--output", "json"],
            UiPathCliProvider.BuildVerbArguments("pack", path));
    }

    [Fact]
    public void BuildVerbArguments_ProjectPathWithMetacharacters_IsSingleToken() {
        var path = @"C:\proj & calc";

        Assert.Equal(
            ["rpa", "validate", "--project-dir", path, "--output", "json"],
            UiPathCliProvider.BuildVerbArguments("validate", path));
    }

    [Fact]
    public void CaptureOutput_RedactsSecrets_AndCapsLength() {
        var (stdout, _) = UiPathCliProvider.CaptureOutput(
            "token=abc123secret", "", maxChars: 50);

        Assert.DoesNotContain("abc123secret", stdout);

        var (capped, _) = UiPathCliProvider.CaptureOutput(new string('y', 500), "", maxChars: 100);
        Assert.True(capped.Length < 500);
        Assert.Contains("[truncated]", capped);
    }

    [Fact]
    public void BuildRawOutputLines_RedactsStdoutAndStderr() {
        var lines = UiPathCliProvider.BuildRawOutputLines(
            "token=abc123secret",
            "password=hunter2");

        Assert.DoesNotContain(lines, l => l.Contains("abc123secret"));
        Assert.DoesNotContain(lines, l => l.Contains("hunter2"));
        Assert.All(lines, l => Assert.Contains("***REDACTED***", l));
    }
}
