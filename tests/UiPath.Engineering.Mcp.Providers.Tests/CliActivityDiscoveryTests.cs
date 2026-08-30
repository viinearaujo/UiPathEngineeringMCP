using UiPath.Engineering.Mcp.Core.Authoring;
using UiPath.Engineering.Mcp.Providers.UiPathCli;

namespace UiPath.Engineering.Mcp.Providers.Tests;

public class CliActivityDiscoveryTests
{
    [Fact]
    public void Sanitize_StripsRejectedChars()
    {
        Assert.Equal("read excel", CliActivityDiscovery.Sanitize("read <excel>"));
        Assert.Equal("click", CliActivityDiscovery.Sanitize("click\""));
    }

    [Fact]
    public async Task FindAsync_ParsesCliStdout()
    {
        var cli = new RecordingCli
        {
            StdOut = """[{ "name": "Click", "package": "UiPath.UIAutomation.Activities", "packageVersion": "24.10.3" }]"""
        };
        var discovery = new CliActivityDiscovery(cli);

        var hits = await discovery.FindAsync("/p", "click");

        Assert.Equal("rpa", cli.LastVerb);
        Assert.Contains("activities find", cli.LastArguments);
        Assert.Contains("--query \"click\"", cli.LastArguments);
        var hit = Assert.Single(hits);
        Assert.Equal("Click", hit.Name);
    }

    [Fact]
    public async Task FindAsync_CliThrows_ReturnsEmpty()
    {
        var discovery = new CliActivityDiscovery(new RecordingCli { ToThrow = new InvalidOperationException("missing") });
        Assert.Empty(await discovery.FindAsync("/p", "click"));
    }

    private sealed class RecordingCli : IUiPathCliProvider
    {
        public string StdOut { get; set; } = "";
        public Exception? ToThrow { get; set; }
        public string? LastVerb { get; private set; }
        public string? LastArguments { get; private set; }

        public Task<UiPathCliResult> ValidateAsync(string projectPath, bool validate, bool build, bool pack, CancellationToken cancellationToken = default) =>
            Task.FromResult(new UiPathCliResult { Success = true });

        public Task<UiPathCliResult> RunAsync(string verb, string arguments, string? workingDirectory = null, CancellationToken cancellationToken = default)
        {
            LastVerb = verb;
            LastArguments = arguments;
            if (ToThrow is not null)
            {
                throw ToThrow;
            }

            return Task.FromResult(new UiPathCliResult { Success = true, StdOut = StdOut });
        }
    }
}
