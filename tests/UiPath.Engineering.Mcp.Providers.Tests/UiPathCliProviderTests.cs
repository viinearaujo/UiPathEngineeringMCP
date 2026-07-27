using Microsoft.Extensions.Options;
using UiPath.Engineering.Mcp.Core.Configuration;
using UiPath.Engineering.Mcp.Providers.UiPathCli;

namespace UiPath.Engineering.Mcp.Providers.Tests;

public class UiPathCliProviderTests
{
    private static UiPathCliProvider CreateSut(string executablePath)
    {
        var options = Options.Create(new UiPathCliOptions
        {
            ExecutablePath = executablePath,
            DefaultTimeoutSeconds = 5
        });
        return new UiPathCliProvider(options);
    }

    [Fact]
    public async Task ValidateAsync_NoStepsRequested_SucceedsWithoutRunningAnything()
    {
        var sut = CreateSut("uip.exe");

        var result = await sut.ValidateAsync("/some/project", restore: false, analyze: false, pack: false);

        Assert.True(result.Success);
        Assert.Equal("Validation completed.", result.Summary);
        Assert.Empty(result.Errors);
        Assert.Equal(string.Empty, result.Command);
    }

    [Fact]
    public async Task ValidateAsync_MissingExecutable_ReturnsStructuredError()
    {
        // A path that cannot be started -> provider must return a structured error, not throw.
        var sut = CreateSut(Path.Combine(Path.GetTempPath(), "definitely-not-a-real-uip-xyz.exe"));

        var result = await sut.ValidateAsync("/some/project", restore: true, analyze: false, pack: false);

        Assert.False(result.Success);
        Assert.NotEmpty(result.Errors);
        Assert.Contains(result.Errors, e => e.Contains("UiPath CLI", StringComparison.OrdinalIgnoreCase)
                                             || e.Contains("start", StringComparison.OrdinalIgnoreCase));
    }
}
