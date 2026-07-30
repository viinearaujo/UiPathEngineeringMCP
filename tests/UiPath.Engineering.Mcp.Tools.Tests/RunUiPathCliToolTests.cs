using Microsoft.Extensions.Options;
using UiPath.Engineering.Mcp.Core.Configuration;
using UiPath.Engineering.Mcp.Providers.UiPathCli;
using UiPath.Engineering.Mcp.Tools;

namespace UiPath.Engineering.Mcp.Tools.Tests;

public class RunUiPathCliToolTests {
    private static RunUiPathCliTool CreateSut(
        FakeUiPathCliProvider cli, FakeFilesystemProvider filesystem, Action<UiPathCliOptions>? configure = null) {
        var options = new UiPathCliOptions();
        configure?.Invoke(options);
        return new RunUiPathCliTool(cli, filesystem, new CliCommandPolicy(options), Options.Create(options));
    }

    [Fact]
    public async Task ReadOnlyCommand_ExecutesAndReturnsStructuredOutput() {
        var cli = new FakeUiPathCliProvider {
            RunResult = new UiPathCliResult {
                Success = true, Command = "uip rpa validate", ExitCode = 0,
                Summary = "'rpa' completed.", StdOut = "all good"
            }
        };
        var sut = CreateSut(cli, new FakeFilesystemProvider());

        var result = await sut.RunUiPathCli("rpa", "validate --project-dir \"C:/proj\" --output json");

        Assert.Equal("success", result.Status);
        Assert.Equal("rpa", cli.LastVerb);
        // The tool prepends the verb so the executed command is `uip rpa validate ...`.
        Assert.Equal("rpa validate --project-dir \"C:/proj\" --output json", cli.LastArguments);
    }

    [Fact]
    public async Task VerbOutsideAllowlist_ReturnsStructuredError_AndNeverRuns() {
        var cli = new FakeUiPathCliProvider();
        var sut = CreateSut(cli, new FakeFilesystemProvider());

        var result = await sut.RunUiPathCli("orx", "assets list");

        Assert.Equal("error", result.Status);
        Assert.Contains(result.ErrorDetails, e => e.ErrorCode == "CLI_VERB_NOT_ALLOWED");
        Assert.Null(cli.LastVerb);
    }

    [Fact]
    public async Task MutatingCommand_WhenDisabled_IsRefused_AndNeverRuns() {
        var cli = new FakeUiPathCliProvider();
        var sut = CreateSut(cli, new FakeFilesystemProvider());

        var result = await sut.RunUiPathCli("solution", "publish --output json");

        Assert.Equal("error", result.Status);
        Assert.Contains(result.ErrorDetails, e => e.ErrorCode == "MUTATING_COMMAND_DISABLED");
        Assert.Null(cli.LastVerb);
    }

    [Fact]
    public async Task MutatingCommand_WhenEnabled_Executes() {
        var cli = new FakeUiPathCliProvider {
            RunResult = new UiPathCliResult { Success = true, Summary = "'solution' completed." }
        };
        var sut = CreateSut(cli, new FakeFilesystemProvider(), o => o.EnableMutatingCommands = true);

        var result = await sut.RunUiPathCli("solution", "pack");

        Assert.Equal("success", result.Status);
        Assert.Equal("solution", cli.LastVerb);
        Assert.Equal("solution pack", cli.LastArguments);
    }

    [Fact]
    public async Task ShellMetacharactersInArguments_ReturnsStructuredError_AndNeverRuns() {
        var cli = new FakeUiPathCliProvider();
        var sut = CreateSut(cli, new FakeFilesystemProvider());

        var result = await sut.RunUiPathCli("rpa", "validate --output json & whoami");

        Assert.Equal("error", result.Status);
        Assert.Contains(result.ErrorDetails, e => e.ErrorCode == "CLI_ARGUMENTS_REJECTED");
        Assert.Null(cli.LastVerb);
        Assert.Null(cli.LastArguments);
    }

    [Fact]
    public async Task UnknownSubcommand_FailsClosed_AsMutating() {
        var cli = new FakeUiPathCliProvider();
        var sut = CreateSut(cli, new FakeFilesystemProvider());

        var result = await sut.RunUiPathCli("rpa", "brand-new-subcommand");

        Assert.Equal("error", result.Status);
        Assert.Contains(result.ErrorDetails, e => e.ErrorCode == "MUTATING_COMMAND_DISABLED");
    }

    [Fact]
    public async Task WorkingDirectoryOutsideAllowedRoots_IsRejected() {
        var cli = new FakeUiPathCliProvider();
        var fs = new FakeFilesystemProvider { Allowed = false };
        var sut = CreateSut(cli, fs);

        var result = await sut.RunUiPathCli("rpa", "validate --project-dir x", workingDirectory: "C:/elsewhere");

        Assert.Equal("error", result.Status);
        Assert.Null(cli.LastVerb);
    }
}
