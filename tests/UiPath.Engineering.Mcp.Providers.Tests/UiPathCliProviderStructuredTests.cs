using Microsoft.Extensions.Options;
using UiPath.Engineering.Mcp.Core.Configuration;
using UiPath.Engineering.Mcp.Providers.UiPathCli;

namespace UiPath.Engineering.Mcp.Providers.Tests;

public class UiPathCliProviderStructuredTests {
    private static UiPathCliProvider CreateSut(string executablePath, Action<UiPathCliOptions>? configure = null) {
        var options = new UiPathCliOptions { ExecutablePath = executablePath, DefaultTimeoutSeconds = 10 };
        configure?.Invoke(options);
        return new UiPathCliProvider(Options.Create(options));
    }

    /// <summary>
    /// Writes a fake uip shim that echoes a fixed JSON envelope (from a sibling file, so no batch
    /// escaping is involved) and records the raw command line it received.
    /// </summary>
    private static async Task<FakeCli> WriteFakeCliAsync(string envelopeJson) {
        var dir = Path.Combine(Path.GetTempPath(), "mcp-uip-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var payload = Path.Combine(dir, "payload.json");
        var commandLine = Path.Combine(dir, "cmdline.txt");
        await File.WriteAllTextAsync(payload, envelopeJson);
        await File.WriteAllTextAsync(Path.Combine(dir, "uip.cmd"), $$"""
            @echo off
            > "{{commandLine}}" echo %*
            type "{{payload}}"
            exit /b 0
            """);
        return new FakeCli(Path.Combine(dir, "uip.cmd"), commandLine, dir);
    }

    private sealed record FakeCli(string Cmd, string CommandLineFile, string Dir) {
        public string ReadCommandLine() => File.Exists(CommandLineFile) ? File.ReadAllText(CommandLineFile) : string.Empty;
        public bool Ran => File.Exists(CommandLineFile);
        public void Cleanup() {
            try { Directory.Delete(Dir, recursive: true); } catch { /* best effort */ }
        }
    }

    private const string SuccessRun = """{"Result":"Success","Data":{"output":"Session ended","errors":[]}}""";

    [Theory]
    [InlineData("run")]
    [InlineData("debug")]
    [InlineData("execution")]
    public async Task RunStructuredAsync_ExecutionVerb_WithoutEnableExecution_IsRefusedBeforeAnyProcessRuns(string subcommand) {
        var fake = await WriteFakeCliAsync(SuccessRun);
        try {
            var sut = CreateSut(fake.Cmd); // EnableExecution defaults false.
            var tokens = subcommand switch {
                "run" => CliVerbArguments.WithRpaVerb(CliVerbArguments.Run(fake.Dir, "Main.xaml")),
                "debug" => CliVerbArguments.WithRpaVerb(CliVerbArguments.DebugStart(fake.Dir, "Main.xaml")),
                _ => CliVerbArguments.WithRpaVerb(CliVerbArguments.ExecutionCancel(fake.Dir))
            };

            var outcome = await sut.RunStructuredAsync("rpa", tokens, fake.Dir);

            Assert.True(outcome.Refused);
            Assert.False(outcome.Cli.Success);
            Assert.Contains(outcome.Cli.Errors, e => e.Contains("EnableExecution", StringComparison.Ordinal));
            // Fail closed: no process ran at all.
            Assert.False(fake.Ran);
        } finally {
            fake.Cleanup();
        }
    }

    [Fact]
    public async Task RunStructuredAsync_ExecutionVerb_WithEnableExecution_RunsAndParsesTheVerdict() {
        var fake = await WriteFakeCliAsync(SuccessRun);
        try {
            var sut = CreateSut(fake.Cmd, o => o.EnableExecution = true);
            var tokens = CliVerbArguments.WithRpaVerb(CliVerbArguments.Run(fake.Dir, "Main.xaml"));

            var outcome = await sut.RunStructuredAsync("rpa", tokens, fake.Dir);

            Assert.False(outcome.Refused);
            Assert.NotNull(outcome.Envelope);
            Assert.True(outcome.Verdict!.Succeeded);
            Assert.True(outcome.Succeeded);
            Assert.Contains("rpa run --file-path Main.xaml", fake.ReadCommandLine());
        } finally {
            fake.Cleanup();
        }
    }

    [Fact]
    public async Task RunStructuredAsync_ReadOnlyVerb_NeedsNoEnableExecutionAndNoEnableMutatingCommands() {
        var fake = await WriteFakeCliAsync("""{"Result":"Success","Data":{"message":"No analyzer rules found."}}""");
        try {
            var sut = CreateSut(fake.Cmd); // Both flags default false.
            var tokens = CliVerbArguments.WithRpaVerb(CliVerbArguments.AnalyzerRulesList(fake.Dir, "Workflow"));

            var outcome = await sut.RunStructuredAsync("rpa", tokens, fake.Dir);

            Assert.False(outcome.Refused);
            Assert.True(outcome.Envelope!.IsSuccess);
            Assert.Contains("analyzer-rules list --scope Workflow", fake.ReadCommandLine());
        } finally {
            fake.Cleanup();
        }
    }

    [Fact]
    public async Task RunStructuredAsync_TokenWithSpaces_StaysOneArgument() {
        var fake = await WriteFakeCliAsync(SuccessRun);
        try {
            var sut = CreateSut(fake.Cmd, o => o.EnableExecution = true);
            var tokens = CliVerbArguments.WithRpaVerb(
                CliVerbArguments.Run(fake.Dir, "My Workflow.xaml", ["message=Hello, world!"]));

            await sut.RunStructuredAsync("rpa", tokens, fake.Dir);

            var commandLine = fake.ReadCommandLine();
            // Quoted so the shim receives each as one argument; SplitQuotedArguments reverses it.
            Assert.Contains("\"My Workflow.xaml\"", commandLine);
            Assert.Contains("\"message=Hello, world!\"", commandLine);
        } finally {
            fake.Cleanup();
        }
    }

    [Fact]
    public async Task RunStructuredAsync_EmptyTokens_IsRefusedWithoutRunningAnything() {
        var sut = CreateSut("definitely-not-a-real-uip-xyz");

        var outcome = await sut.RunStructuredAsync("rpa", []);

        Assert.True(outcome.Refused);
        Assert.False(outcome.Cli.Success);
        Assert.Contains(outcome.Cli.Errors, e => e.Contains("command token", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RunStructuredAsync_TokenWithNewline_IsRejected() {
        var sut = CreateSut("definitely-not-a-real-uip-xyz", o => o.EnableExecution = true);

        var outcome = await sut.RunStructuredAsync("rpa", ["rpa", "run", "--file-path", "Main.xaml\nwhoami"]);

        Assert.True(outcome.Refused);
        Assert.Contains(outcome.Cli.Errors, e => e.Contains("control characters", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RunStructuredAsync_EnvelopeIsParsedFromUncappedStdout() {
        // A payload larger than MaxOutputChars must still parse: the verdict reads the uncapped
        // stdout, so a long run response is never truncated into unparseable JSON.
        var bigMessage = new string('x', 200);
        var fake = await WriteFakeCliAsync(
            "{\"Result\":\"Success\",\"Data\":{\"output\":\"Session ended\",\"errors\":[],"
            + "\"logEntries\":[{\"source\":\"Debug\",\"level\":\"Information\",\"message\":\"" + bigMessage + "\"}]}}");
        try {
            var sut = new UiPathCliProvider(Options.Create(new UiPathCliOptions {
                ExecutablePath = fake.Cmd,
                DefaultTimeoutSeconds = 10,
                EnableExecution = true,
                MaxOutputChars = 32
            }));
            var tokens = CliVerbArguments.WithRpaVerb(CliVerbArguments.Run(fake.Dir, "Main.xaml"));

            var outcome = await sut.RunStructuredAsync("rpa", tokens, fake.Dir);

            // The capped StdOut is truncated, but the parsed verdict still read the whole entry.
            Assert.Contains("[truncated]", outcome.Cli.StdOut);
            Assert.Equal(bigMessage, outcome.Verdict!.LogEntries[0].Message);
        } finally {
            fake.Cleanup();
        }
    }

    [Fact]
    public async Task RunStructuredAsync_SecretInVerdictFields_IsRedacted() {
        // The envelope is parsed un-redacted (redacting raw JSON would corrupt it), but every
        // string field that can carry process output is redacted as it is read.
        var fake = await WriteFakeCliAsync(
            """{"Result":"Success","Data":{"output":"","hasErrors":true,"errorMessage":"Auth failed: token=abc123secret was rejected"}}""");
        try {
            var sut = CreateSut(fake.Cmd, o => o.EnableExecution = true);
            var tokens = CliVerbArguments.WithRpaVerb(CliVerbArguments.Run(fake.Dir, "Main.xaml"));

            var outcome = await sut.RunStructuredAsync("rpa", tokens, fake.Dir);

            Assert.DoesNotContain("abc123secret", outcome.Verdict!.ErrorMessage);
            Assert.Contains("***REDACTED***", outcome.Verdict.ErrorMessage);
        } finally {
            fake.Cleanup();
        }
    }

    [Fact]
    public async Task RunStructuredAsync_MissingExecutable_ReturnsStructuredErrorWithoutThrowing() {
        var sut = CreateSut("definitely-not-a-real-uip-xyz");
        var tokens = CliVerbArguments.WithRpaVerb(CliVerbArguments.AnalyzerRulesList(Path.GetTempPath(), "Workflow"));

        var outcome = await sut.RunStructuredAsync("rpa", tokens, Path.GetTempPath());

        Assert.False(outcome.Refused);
        Assert.False(outcome.Cli.Success);
        Assert.Null(outcome.Envelope);
        Assert.Contains(outcome.Cli.Errors, e => e.Contains("@uipath/cli", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunStructuredAsync_BannerTextBeforeTheEnvelope_StillParses() {
        // The npm shim can print update chatter on stdout ahead of the JSON payload.
        var fake = await WriteFakeCliAsync(SuccessRun);
        try {
            var bannerCmd = Path.Combine(fake.Dir, "uip-banner.cmd");
            await File.WriteAllTextAsync(bannerCmd, $$"""
                @echo off
                echo Checking for updates.
                type "{{Path.Combine(fake.Dir, "payload.json")}}"
                exit /b 0
                """);
            var sut = CreateSut(bannerCmd, o => o.EnableExecution = true);
            var tokens = CliVerbArguments.WithRpaVerb(CliVerbArguments.Run(fake.Dir, "Main.xaml"));

            var outcome = await sut.RunStructuredAsync("rpa", tokens, fake.Dir);

            Assert.NotNull(outcome.Envelope);
            Assert.True(outcome.Succeeded);
        } finally {
            fake.Cleanup();
        }
    }

    [Theory]
    [InlineData("run --file-path Main.xaml --output json")]
    [InlineData("debug start --file-path Main.xaml --output json")]
    [InlineData("debug continue --output json")]
    [InlineData("execution cancel --output json")]
    public async Task RunAsync_ExecutionVerb_WithEnableMutatingCommandsOnly_IsStillRefused(string arguments) {
        // The run_ui_path_cli hatch passes verb-prefixed arguments through RunAsync. Turning on
        // EnableMutatingCommands must NOT make automation executable: EnableExecution is the only
        // switch that opens it, and the provider enforces that at the process-start choke point.
        var fake = await WriteFakeCliAsync(SuccessRun);
        try {
            var sut = CreateSut(fake.Cmd, o => o.EnableMutatingCommands = true);

            var result = await sut.RunAsync("rpa", "rpa " + arguments, fake.Dir);

            Assert.False(result.Success);
            Assert.Contains(result.Errors, e => e.Contains("EnableExecution", StringComparison.Ordinal));
            Assert.False(fake.Ran);
        } finally {
            fake.Cleanup();
        }
    }

    [Fact]
    public async Task RunAsync_ExecutionVerb_WithEnableExecution_Runs() {
        var fake = await WriteFakeCliAsync(SuccessRun);
        try {
            var sut = CreateSut(fake.Cmd, o => {
                o.EnableMutatingCommands = true;
                o.EnableExecution = true;
            });

            var result = await sut.RunAsync("rpa", "rpa run --file-path Main.xaml --output json", fake.Dir);

            Assert.True(fake.Ran);
            Assert.True(result.Success);
        } finally {
            fake.Cleanup();
        }
    }

    [Fact]
    public async Task RunAsync_ReadOnlyVerb_IsUnaffectedByTheExecutionGate() {
        var fake = await WriteFakeCliAsync("""{"Result":"Success","Data":{}}""");
        try {
            var sut = CreateSut(fake.Cmd); // Both flags default false.

            var result = await sut.RunAsync("rpa", "rpa validate --project-dir . --output json", fake.Dir);

            Assert.True(fake.Ran);
            Assert.True(result.Success);
        } finally {
            fake.Cleanup();
        }
    }

    [Fact]
    public async Task RunStructuredAsync_HonoursTheRequestedTimeout() {
        // A shim that sleeps past the requested timeout must come back as a timeout, not a hang.
        var dir = Path.Combine(Path.GetTempPath(), "mcp-uip-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var cmd = Path.Combine(dir, "uip.cmd");
        await File.WriteAllTextAsync(cmd, """
            @echo off
            ping -n 20 127.0.0.1 > nul
            echo {"Result":"Success","Data":{}}
            exit /b 0
            """);
        try {
            var sut = CreateSut(cmd);
            var tokens = CliVerbArguments.WithRpaVerb(CliVerbArguments.AnalyzerRulesList(dir, "Workflow"));

            var outcome = await sut.RunStructuredAsync("rpa", tokens, dir, timeoutSeconds: 1);

            Assert.False(outcome.Cli.Success);
            Assert.Contains(outcome.Cli.Errors, e => e.Contains("exceeded the 1s timeout", StringComparison.Ordinal));
        } finally {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }
}


