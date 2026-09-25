using UiPath.Engineering.Mcp.Core.Jobs;
using UiPath.Engineering.Mcp.Core.Models;
using UiPath.Engineering.Mcp.Tools;

namespace UiPath.Engineering.Mcp.Tools.Tests;

public class ControlDebugSessionToolTests {
    private static (ControlDebugSessionTool Sut, RecordingStructuredCli Cli, BackgroundJobStore Jobs) CreateSut(
        string stdOut = """{"Result":"Success","Data":{"output":"","hasErrors":false,"debugState":"None"}}""",
        bool execution = false, string workflow = "Main.xaml") {
        var cli = new RecordingStructuredCli { StdOut = stdOut };
        var filesystem = CliToolFixtures.ProjectOnlyFilesystem()
            .WithFile(Path.Combine(CliToolFixtures.ProjectPath, workflow.Replace('/', Path.DirectorySeparatorChar)));
        var jobs = BackgroundJobs.NewStore();
        return (new ControlDebugSessionTool(cli, filesystem, CliToolFixtures.Policy(execution: execution), jobs), cli, jobs);
    }

    [Fact]
    public async Task ExecutionDisabledByDefault_RefusesStartWithoutRunningAnything() {
        var (sut, cli, jobs) = CreateSut(execution: false);

        var result = await BackgroundJobs.AwaitFinished(jobs, await sut.ControlDebugSession(CliToolFixtures.ProjectPath, "start", filePath: "Main.xaml"));

        Assert.Equal("error", result.Status);
        var error = Assert.Single(result.ErrorDetails);
        Assert.Equal("EXECUTION_DISABLED", error.ErrorCode);
        Assert.Contains("UiPathCli:EnableExecution", error.FixHint);
        Assert.Empty(cli.Calls);
    }

    [Fact]
    public async Task ExecutionDisabledByDefault_RefusesReadOnlyLookingStateProbeToo() {
        // `debug state` looks read-only but sits on the execution surface: the whole debug group
        // drives a live session, so it stays behind the same gate.
        var (sut, cli, jobs) = CreateSut(execution: false);

        var result = await BackgroundJobs.AwaitFinished(jobs, await sut.ControlDebugSession(CliToolFixtures.ProjectPath, "state"));

        Assert.Equal("error", result.Status);
        Assert.Contains(result.ErrorDetails, e => e.ErrorCode == "EXECUTION_DISABLED");
        Assert.Empty(cli.Calls);
    }

    [Fact]
    public async Task ExecutionDisabledByDefault_RefusesCancel() {
        var (sut, cli, jobs) = CreateSut(execution: false);

        var result = await BackgroundJobs.AwaitFinished(jobs, await sut.ControlDebugSession(CliToolFixtures.ProjectPath, "cancel"));

        Assert.Equal("error", result.Status);
        Assert.Contains(result.ErrorDetails, e => e.ErrorCode == "EXECUTION_DISABLED");
        Assert.Empty(cli.Calls);
    }

    [Fact]
    public async Task Start_RunsTheDebugStartVerb() {
        var (sut, cli, jobs) = CreateSut(execution: true);

        var result = await BackgroundJobs.AwaitFinished(jobs, await sut.ControlDebugSession(CliToolFixtures.ProjectPath, "start", filePath: "Main.xaml"));

        Assert.Equal("success", result.Status);
        Assert.Equal(
            ["rpa", "debug", "start", "--file-path", "Main.xaml", "--project-dir", CliToolFixtures.ProjectPath, "--output", "json"],
            cli.LastTokens);
    }

    [Fact]
    public async Task Start_WithoutFilePath_IsRefused() {
        var (sut, cli, jobs) = CreateSut(execution: true);

        var result = await BackgroundJobs.AwaitFinished(jobs, await sut.ControlDebugSession(CliToolFixtures.ProjectPath, "start"));

        Assert.Equal("error", result.Status);
        Assert.Contains(result.ErrorDetails, e => e.ErrorCode == "INVALID_ARGUMENT");
        Assert.Empty(cli.Calls);
    }

    [Fact]
    public async Task Start_WithBreakpoints_PassesRepeatableItems() {
        var (sut, cli, jobs) = CreateSut(
            """{"Result":"Success","Data":{"runResult":"{\"output\":\"\",\"hasErrors\":false,\"debugState\":\"Paused\",\"debugDetails\":\"{\\\"Activity\\\":\\\"Assign x\\\"}\"}"}}""",
            execution: true);

        var result = await BackgroundJobs.AwaitFinished(jobs, await sut.ControlDebugSession(CliToolFixtures.ProjectPath, "start", filePath: "Main.xaml",
            breakpoints: ["workflowFile=Main.xaml,activityIdRef=Assign_1", "workflowFile=Main.xaml,activityIdRef=Click_2,hitCount:=3"]));

        Assert.Equal(2, cli.LastTokens!.Count(t => t == "--breakpoints"));
        var data = CliToolFixtures.Data<DebugPayload>(result)!;
        Assert.Equal("Paused", data.DebugState);
        Assert.True(data.AwaitingDecision);
    }

    [Fact]
    public async Task PausedState_IsReportedAsAwaitingDecisionNotFailure() {
        var (sut, _, jobs) = CreateSut(
            """{"Result":"Success","Data":{"runResult":"{\"output\":\"\",\"hasErrors\":false,\"debugState\":\"Paused\",\"debugDetails\":\"{\\\"Activity\\\":\\\"Assign x\\\",\\\"ActivityId\\\":\\\"1.5\\\"}\"}"}}""",
            execution: true);

        var result = await BackgroundJobs.AwaitFinished(jobs, await sut.ControlDebugSession(CliToolFixtures.ProjectPath, "start", filePath: "Main.xaml"));
        var data = CliToolFixtures.Data<DebugPayload>(result)!;

        // A paused session is undecided, not failed: status stays success so the caller steps on.
        Assert.Equal("success", result.Status);
        Assert.False(data.Succeeded);
        Assert.True(data.AwaitingDecision);
        Assert.Contains(result.Warnings, w => w.Contains("debugDetails", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SuspendedState_IsCheckedBeforeHasErrors() {
        // The documented trap: Suspended means an exception awaits a decision while HasErrors is
        // still false, so HasErrors alone would report a green run.
        var (sut, _, jobs) = CreateSut(
            """{"Result":"Success","Data":{"runResult":"{\"output\":\"\",\"hasErrors\":false,\"errorMessage\":\"Execution suspended on an unhandled exception; the session is still alive.\",\"debugState\":\"Suspended\"}"}}""",
            execution: true);

        var result = await BackgroundJobs.AwaitFinished(jobs, await sut.ControlDebugSession(CliToolFixtures.ProjectPath, "state"));
        var data = CliToolFixtures.Data<DebugPayload>(result)!;

        Assert.Equal("success", result.Status);
        Assert.False(data.HasErrors);
        Assert.Equal("Suspended", data.DebugState);
        Assert.True(data.AwaitingDecision);
        Assert.False(data.Succeeded);
        Assert.Contains("Suspended", result.Summary, StringComparison.Ordinal);
        Assert.Contains(result.Warnings, w => w.Contains("continue-retry", StringComparison.Ordinal));
        Assert.Contains("Suspended", data.VerdictNote, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DebugDetails_IsParsedFromTheJsonStringSnapshot() {
        var (sut, _, jobs) = CreateSut(
            """{"Result":"Success","Data":{"runResult":"{\"output\":\"\",\"hasErrors\":false,\"debugState\":\"Paused\",\"debugDetails\":\"{\\\"Activity\\\":\\\"Assign x\\\",\\\"ActivityId\\\":\\\"1.5\\\"}\"}"}}""",
            execution: true);

        var result = await BackgroundJobs.AwaitFinished(jobs, await sut.ControlDebugSession(CliToolFixtures.ProjectPath, "state"));
        var data = CliToolFixtures.Data<DebugPayload>(result)!;

        Assert.Equal("Assign x", data.DebugDetails!.Value.GetProperty("Activity").GetString());
        Assert.Equal("1.5", data.DebugDetails.Value.GetProperty("ActivityId").GetString());
    }

    [Fact]
    public async Task RunningState_WarnsToPollAgain() {
        var (sut, _, jobs) = CreateSut(
            """{"Result":"Success","Data":{"runResult":"{\"output\":\"\",\"hasErrors\":false,\"debugState\":\"Running\"}"}}""",
            execution: true);

        var result = await BackgroundJobs.AwaitFinished(jobs, await sut.ControlDebugSession(CliToolFixtures.ProjectPath, "state"));

        Assert.Contains(result.Warnings, w => w.Contains("command=state", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("state")]
    [InlineData("step-over")]
    [InlineData("step-into")]
    [InlineData("step-out")]
    [InlineData("continue")]
    [InlineData("continue-retry")]
    [InlineData("continue-ignore")]
    [InlineData("resume")]
    [InlineData("break")]
    [InlineData("restart-from-top")]
    public async Task EveryMidSessionCommand_MapsToItsDebugVerb(string command) {
        var (sut, cli, jobs) = CreateSut(
            """{"Result":"Success","Data":{"runResult":"{\"output\":\"\",\"hasErrors\":false,\"debugState\":\"Completed\"}"}}""",
            execution: true);

        var result = await BackgroundJobs.AwaitFinished(jobs, await sut.ControlDebugSession(CliToolFixtures.ProjectPath, command));

        Assert.Equal("success", result.Status);
        Assert.Equal(["rpa", "debug", command, "--project-dir", CliToolFixtures.ProjectPath, "--output", "json"], cli.LastTokens);
    }

    [Fact]
    public async Task WaitTimeoutSeconds_IsForwarded() {
        var (sut, cli, jobs) = CreateSut(execution: true);

        await BackgroundJobs.AwaitFinished(jobs, await sut.ControlDebugSession(CliToolFixtures.ProjectPath, "state", waitTimeoutSeconds: 0));

        Assert.Equal("0", CliToolFixtures.TokenAfter(cli.LastTokens, "--wait-timeout-seconds"));
    }

    [Fact]
    public async Task Cancel_MapsToExecutionCancel() {
        var (sut, cli, jobs) = CreateSut(
            """{"Result":"Success","Data":{"runResult":"{\"output\":\"\",\"hasErrors\":false,\"debugState\":\"Completed\"}"}}""",
            execution: true);

        var result = await BackgroundJobs.AwaitFinished(jobs, await sut.ControlDebugSession(CliToolFixtures.ProjectPath, "cancel"));

        Assert.Equal("success", result.Status);
        Assert.Equal(
            ["rpa", "execution", "cancel", "--project-dir", CliToolFixtures.ProjectPath, "--output", "json"],
            cli.LastTokens);
        // Cancel ends the session, so no "still alive" warning.
        Assert.DoesNotContain(result.Warnings, w => w.Contains("command=cancel", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SetBreakpoints_ReplacesTheWholeSet() {
        var (sut, cli, jobs) = CreateSut(
            """{"Result":"Success","Data":{"runResult":"{\"output\":\"\",\"hasErrors\":false,\"debugState\":\"Paused\"}"}}""",
            execution: true);

        var result = await BackgroundJobs.AwaitFinished(jobs, await sut.ControlDebugSession(CliToolFixtures.ProjectPath, "set-breakpoints",
            breakpoints: ["workflowFile=Main.xaml,activityIdRef=A_1"]));

        Assert.Equal("success", result.Status);
        Assert.Equal(["rpa",
            "debug",
            "set-breakpoints",
            "--breakpoints",
            "workflowFile=Main.xaml,activityIdRef=A_1",
            "--project-dir",
            CliToolFixtures.ProjectPath,
            "--output",
            "json"], cli.LastTokens);
    }

    [Fact]
    public async Task SetBreakpoints_WithoutBreakpoints_IsRefused() {
        var (sut, cli, jobs) = CreateSut(execution: true);

        var result = await BackgroundJobs.AwaitFinished(jobs, await sut.ControlDebugSession(CliToolFixtures.ProjectPath, "set-breakpoints"));

        Assert.Equal("error", result.Status);
        Assert.Contains(result.ErrorDetails, e => e.ErrorCode == "INVALID_ARGUMENT");
        Assert.Empty(cli.Calls);
    }

    [Fact]
    public async Task UnknownCommand_IsRefused() {
        var (sut, cli, jobs) = CreateSut(execution: true);

        var result = await BackgroundJobs.AwaitFinished(jobs, await sut.ControlDebugSession(CliToolFixtures.ProjectPath, "step-sideways"));

        Assert.Equal("error", result.Status);
        Assert.Contains(result.ErrorDetails, e => e.ErrorCode == "INVALID_ARGUMENT");
        Assert.Empty(cli.Calls);
    }

    [Fact]
    public async Task WaitTimeoutWithoutEnoughCliTimeoutMargin_IsRefused() {
        // The shell timeout must exceed --wait-timeout-seconds by >= 30s or the CLI is killed
        // before it can cancel cleanly.
        var (sut, cli, jobs) = CreateSut(execution: true);

        var result = await BackgroundJobs.AwaitFinished(jobs, await sut.ControlDebugSession(CliToolFixtures.ProjectPath, "state",
            waitTimeoutSeconds: 300, timeoutSeconds: 300));

        Assert.Equal("error", result.Status);
        Assert.Contains(result.ErrorDetails, e => e.ErrorCode == "INVALID_ARGUMENT");
        Assert.Empty(cli.Calls);
    }

    [Fact]
    public async Task CompletedFailure_IsReportedAsError() {
        var (sut, _, jobs) = CreateSut(
            """{"Result":"Success","Data":{"runResult":"{\"output\":\"\",\"hasErrors\":true,\"errorMessage\":\"Source: Assign_1\\nMessage: boom\",\"debugState\":\"Completed\"}"}}""",
            execution: true);

        var result = await BackgroundJobs.AwaitFinished(jobs, await sut.ControlDebugSession(CliToolFixtures.ProjectPath, "start", filePath: "Main.xaml"));

        Assert.Equal("error", result.Status);
        Assert.Contains(result.Errors, e => e.Contains("boom", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SessionStillAlive_WarnsToCancel() {
        var (sut, _, jobs) = CreateSut(
            """{"Result":"Success","Data":{"runResult":"{\"output\":\"\",\"hasErrors\":false,\"debugState\":\"Paused\"}"}}""",
            execution: true);

        var result = await BackgroundJobs.AwaitFinished(jobs, await sut.ControlDebugSession(CliToolFixtures.ProjectPath, "step-over"));

        Assert.Contains(result.Warnings, w => w.Contains("command=cancel", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ProfilingOutputDirectory_IsSurfaced() {
        var (sut, _, jobs) = CreateSut(
            """{"Result":"Success","Data":{"runResult":"{\"output\":\"\",\"hasErrors\":false,\"debugState\":\"Completed\",\"profiling\":{\"outputDirectory\":\"C:\\\\runs\\\\1\"}}"}}""",
            execution: true);

        var result = await BackgroundJobs.AwaitFinished(jobs, await sut.ControlDebugSession(CliToolFixtures.ProjectPath, "start", filePath: "Main.xaml", profiling: true));

        Assert.Equal(@"C:\runs\1", CliToolFixtures.Data<DebugPayload>(result)!.ProfilingOutputDirectory);
    }

    [Fact]
    public async Task FailureEnvelope_IsReportedAsError() {
        var (sut, _, jobs) = CreateSut("""{"Result":"Failure","Message":"No debug session is active."}""", execution: true);

        var result = await BackgroundJobs.AwaitFinished(jobs, await sut.ControlDebugSession(CliToolFixtures.ProjectPath, "continue"));

        Assert.Equal("error", result.Status);
        Assert.Contains(result.Errors, e => e.Contains("No debug session", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnparseablePayload_ReturnsCliUnparseableResponse() {
        var (sut, _, jobs) = CreateSut("not json", execution: true);

        var result = await BackgroundJobs.AwaitFinished(jobs, await sut.ControlDebugSession(CliToolFixtures.ProjectPath, "state"));

        Assert.Equal("error", result.Status);
        Assert.Contains(result.ErrorDetails, e => e.ErrorCode == "CLI_UNPARSEABLE_RESPONSE");
    }

    [Fact]
    public async Task MissingProjectJson_IsRefusedBeforeTheExecutionGate() {
        var cli = new RecordingStructuredCli();
        var jobs = BackgroundJobs.NewStore();
        var sut = new ControlDebugSessionTool(cli, new FakeFilesystemProvider { ProjectJson = null }, CliToolFixtures.Policy(execution: true), jobs);

        var result = await BackgroundJobs.AwaitFinished(jobs, await sut.ControlDebugSession(CliToolFixtures.ProjectPath, "state"));

        Assert.Equal("error", result.Status);
        Assert.Contains(result.ErrorDetails, e => e.ErrorCode == "PROJECT_JSON_NOT_FOUND");
        Assert.Empty(cli.Calls);
    }

    private sealed record DebugPayload(
        bool Succeeded, string? EnvelopeResult, int ExitCode, bool? HasErrors, string? ErrorMessage,
        string? Output, List<object> Errors, string? DebugState, System.Text.Json.JsonElement? DebugDetails,
        string? ProfilingOutputDirectory, bool AwaitingDecision, string PayloadShape, string Command,
        List<object> LogEntries, bool LogEntriesTruncated, string VerdictNote);
}
