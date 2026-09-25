using UiPath.Engineering.Mcp.Core.Jobs;
using UiPath.Engineering.Mcp.Core.Models;
using UiPath.Engineering.Mcp.Tools;

namespace UiPath.Engineering.Mcp.Tools.Tests;

public class RunWorkflowToolTests {
    private const string FlatSuccess = """
        {"Result":"Success","Code":"ToolResult","Data":{"output":"Session ended","errors":[],"logEntries":[{"source":"Debug","level":"Information","message":"5 + 5 = 10"}]}}
        """;

    private const string NestedFailure = """
        {"Result":"Success","Data":{"runResult":"{\"output\":\"\",\"hasErrors\":true,\"errorMessage\":\"Source: HttpRequest_1\\nMessage: boom\",\"debugState\":\"Completed\"}"}}
        """;

    private static (RunWorkflowTool Sut, RecordingStructuredCli Cli, SelectiveFilesystem Fs, BackgroundJobStore Jobs) CreateSut(
        string stdOut = FlatSuccess, bool execution = false, string workflow = "Main.xaml") {
        var cli = new RecordingStructuredCli { StdOut = stdOut };
        var filesystem = CliToolFixtures.ProjectOnlyFilesystem()
            .WithFile(Path.Combine(CliToolFixtures.ProjectPath, workflow.Replace('/', Path.DirectorySeparatorChar)));
        var jobs = BackgroundJobs.NewStore();
        return (new RunWorkflowTool(cli, filesystem, CliToolFixtures.Policy(execution: execution), jobs), cli, filesystem, jobs);
    }

    [Fact]
    public async Task ExecutionDisabledByDefault_IsRefusedWithoutRunningAnything() {
        var (sut, cli, _, jobs) = CreateSut(FlatSuccess, execution: false);

        var result = await BackgroundJobs.AwaitFinished(jobs, await sut.RunWorkflow(CliToolFixtures.ProjectPath, "Main.xaml"));

        Assert.Equal("error", result.Status);
        var error = Assert.Single(result.ErrorDetails);
        Assert.Equal("EXECUTION_DISABLED", error.ErrorCode);
        Assert.Contains("UiPathCli:EnableExecution", error.FixHint);
        Assert.Equal("validate_project", error.SuggestedTool);
        Assert.Empty(cli.Calls);
    }

    [Fact]
    public async Task ExecutionEnabled_RunsTheWorkflowAndReportsSuccess() {
        var (sut, cli, _, jobs) = CreateSut(FlatSuccess, execution: true);

        var result = await BackgroundJobs.AwaitFinished(jobs, await sut.RunWorkflow(CliToolFixtures.ProjectPath, "Main.xaml"));

        Assert.Equal("success", result.Status);
        Assert.True(CliToolFixtures.Data<RunPayload>(result)!.Succeeded);
        Assert.Equal(
            ["rpa", "run", "--file-path", "Main.xaml", "--project-dir", CliToolFixtures.ProjectPath, "--output", "json"],
            cli.LastTokens);
    }

    [Fact]
    public async Task FilePath_IsPassedRelativeToTheProjectRoot() {
        // An absolute --file-path with an absolute --project-dir falsely fails (separator mismatch),
        // so the relative form is what must reach the CLI.
        var (sut, cli, _, jobs) = CreateSut(FlatSuccess, execution: true);

        await BackgroundJobs.AwaitFinished(jobs, await sut.RunWorkflow(CliToolFixtures.ProjectPath, "Main.xaml"));

        Assert.Equal("Main.xaml", CliToolFixtures.TokenAfter(cli.LastTokens, "--file-path"));
    }

    [Fact]
    public async Task NestedSubfolderWorkflow_KeepsItsRelativePath() {
        var (sut, cli, _, jobs) = CreateSut(FlatSuccess, execution: true, workflow: "Workflows/Process.xaml");

        var result = await BackgroundJobs.AwaitFinished(jobs, await sut.RunWorkflow(CliToolFixtures.ProjectPath, "Workflows/Process.xaml"));

        Assert.Equal("success", result.Status);
        Assert.Equal("Workflows/Process.xaml", CliToolFixtures.TokenAfter(cli.LastTokens, "--file-path"));
    }

    [Fact]
    public async Task AbsoluteFilePathInsideTheProject_IsAccepted() {
        var (sut, cli, _, jobs) = CreateSut(FlatSuccess, execution: true);
        var absolute = Path.Combine(CliToolFixtures.ProjectPath, "Main.xaml");

        var result = await BackgroundJobs.AwaitFinished(jobs, await sut.RunWorkflow(CliToolFixtures.ProjectPath, absolute));

        Assert.Equal("success", result.Status);
        Assert.Equal("Main.xaml", CliToolFixtures.TokenAfter(cli.LastTokens, "--file-path"));
    }

    [Fact]
    public async Task FilePathOutsideTheProject_IsRefused() {
        var (sut, cli, _, jobs) = CreateSut(FlatSuccess, execution: true);

        var result = await BackgroundJobs.AwaitFinished(jobs, await sut.RunWorkflow(CliToolFixtures.ProjectPath, @"C:\elsewhere\Main.xaml"));

        Assert.Equal("error", result.Status);
        Assert.Contains(result.ErrorDetails, e => e.ErrorCode == "PATH_NOT_ALLOWED");
        Assert.Empty(cli.Calls);
    }

    [Fact]
    public async Task TraversalOutOfTheProject_IsRefused() {
        var (sut, cli, _, jobs) = CreateSut(FlatSuccess, execution: true);

        var result = await BackgroundJobs.AwaitFinished(jobs, await sut.RunWorkflow(CliToolFixtures.ProjectPath, "../OtherProject/Main.xaml"));

        Assert.Equal("error", result.Status);
        Assert.Contains(result.ErrorDetails, e => e.ErrorCode == "PATH_NOT_ALLOWED");
        Assert.Empty(cli.Calls);
    }

    [Fact]
    public async Task MissingWorkflow_IsRefusedWithoutExecuting() {
        var (sut, cli, _, jobs) = CreateSut(FlatSuccess, execution: true);

        var result = await BackgroundJobs.AwaitFinished(jobs, await sut.RunWorkflow(CliToolFixtures.ProjectPath, "Nope.xaml"));

        Assert.Equal("error", result.Status);
        Assert.Contains(result.ErrorDetails, e => e.ErrorCode == "OPERATION_FAILED");
        Assert.Empty(cli.Calls);
    }

    [Fact]
    public async Task EmptyFilePath_IsRefused() {
        var (sut, cli, _, jobs) = CreateSut(FlatSuccess, execution: true);

        var result = await BackgroundJobs.AwaitFinished(jobs, await sut.RunWorkflow(CliToolFixtures.ProjectPath, "  "));

        Assert.Equal("error", result.Status);
        Assert.Contains(result.ErrorDetails, e => e.ErrorCode == "INVALID_ARGUMENT");
        Assert.Empty(cli.Calls);
    }

    [Fact]
    public async Task InputArguments_ArePassedAsRepeatablePairs() {
        var (sut, cli, _, jobs) = CreateSut(FlatSuccess, execution: true);

        await BackgroundJobs.AwaitFinished(jobs, await sut.RunWorkflow(CliToolFixtures.ProjectPath, "Main.xaml",
            inputArguments: ["name=John", "retries:=3", "payload=@args.json"]));

        var tokens = cli.LastTokens!;
        Assert.Equal(3, tokens.Count(t => t == "--input-arguments"));
        Assert.Contains("name=John", tokens);
        Assert.Contains("retries:=3", tokens);
        Assert.Contains("payload=@args.json", tokens);
    }

    [Fact]
    public async Task InputArgumentWithSpaces_StaysOneToken() {
        var (sut, cli, _, jobs) = CreateSut(FlatSuccess, execution: true);

        await BackgroundJobs.AwaitFinished(jobs, await sut.RunWorkflow(CliToolFixtures.ProjectPath, "Main.xaml", inputArguments: ["message=Hello, world!"]));

        Assert.Contains("message=Hello, world!", cli.LastTokens!);
    }

    [Fact]
    public async Task InputArgumentWithDoubleQuote_IsRefused() {
        // Windows PowerShell 5.1 strips inline double quotes; such values must travel via a file.
        var (sut, cli, _, jobs) = CreateSut(FlatSuccess, execution: true);

        var result = await BackgroundJobs.AwaitFinished(jobs, await sut.RunWorkflow(CliToolFixtures.ProjectPath, "Main.xaml", inputArguments: ["""json={"k":"v"}"""]));

        Assert.Equal("error", result.Status);
        var error = Assert.Single(result.ErrorDetails);
        Assert.Equal("INVALID_ARGUMENT", error.ErrorCode);
        Assert.Contains("key=@file", error.FixHint);
        Assert.Empty(cli.Calls);
    }

    [Theory]
    [InlineData("not-a-pair")]
    [InlineData("=value")]
    public async Task MalformedInputArgument_IsRefused(string item) {
        var (sut, cli, _, jobs) = CreateSut(FlatSuccess, execution: true);

        var result = await BackgroundJobs.AwaitFinished(jobs, await sut.RunWorkflow(CliToolFixtures.ProjectPath, "Main.xaml", inputArguments: [item]));

        Assert.Equal("error", result.Status);
        Assert.Contains(result.ErrorDetails, e => e.ErrorCode == "INVALID_ARGUMENT");
        Assert.Empty(cli.Calls);
    }

    [Fact]
    public async Task InputArgumentWithNewline_IsRefused() {
        var (sut, cli, _, jobs) = CreateSut(FlatSuccess, execution: true);

        var result = await BackgroundJobs.AwaitFinished(jobs, await sut.RunWorkflow(CliToolFixtures.ProjectPath, "Main.xaml", inputArguments: ["key=a\nwhoami"]));

        Assert.Equal("error", result.Status);
        Assert.Contains(result.ErrorDetails, e => e.ErrorCode == "CLI_ARGUMENTS_REJECTED");
        Assert.Empty(cli.Calls);
    }

    [Fact]
    public async Task BareAtFile_IsAccepted() {
        var (sut, cli, _, jobs) = CreateSut(FlatSuccess, execution: true);

        await BackgroundJobs.AwaitFinished(jobs, await sut.RunWorkflow(CliToolFixtures.ProjectPath, "Main.xaml", inputArguments: ["@args.json"]));

        Assert.Contains("@args.json", cli.LastTokens!);
    }

    [Fact]
    public async Task SkipBuildAndProfiling_AreEmittedAsFlags() {
        var (sut, cli, _, jobs) = CreateSut("""
            {"Result":"Success","Data":{"runResult":"{\"output\":\"\",\"hasErrors\":false,\"profiling\":{\"outputDirectory\":\"C:\\\\runs\\\\1\"}}"}}
            """, execution: true);

        var result = await BackgroundJobs.AwaitFinished(jobs, await sut.RunWorkflow(CliToolFixtures.ProjectPath, "Main.xaml",
            skipBuild: true, profiling: true, profilingMode: "stream"));

        Assert.Contains("--skip-build", cli.LastTokens!);
        Assert.Contains("--profiling", cli.LastTokens!);
        Assert.Equal(@"C:\runs\1", CliToolFixtures.Data<RunPayload>(result)!.ProfilingOutputDirectory);
    }

    [Fact]
    public async Task ProfilingRequestedButNotReturned_Warns() {
        var (sut, _, _, jobs) = CreateSut(FlatSuccess, execution: true);

        var result = await BackgroundJobs.AwaitFinished(jobs, await sut.RunWorkflow(CliToolFixtures.ProjectPath, "Main.xaml", profiling: true));

        Assert.Contains(result.Warnings, w => w.Contains("EnableProfiling", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InvalidLogLevel_IsRefused() {
        var (sut, cli, _, jobs) = CreateSut(FlatSuccess, execution: true);

        var result = await BackgroundJobs.AwaitFinished(jobs, await sut.RunWorkflow(CliToolFixtures.ProjectPath, "Main.xaml", logLevel: "Chatty"));

        Assert.Equal("error", result.Status);
        Assert.Empty(cli.Calls);
    }

    [Fact]
    public async Task InvalidProfilingMode_IsRefused() {
        var (sut, cli, _, jobs) = CreateSut(FlatSuccess, execution: true);

        var result = await BackgroundJobs.AwaitFinished(jobs, await sut.RunWorkflow(CliToolFixtures.ProjectPath, "Main.xaml", profilingMode: "live"));

        Assert.Equal("error", result.Status);
        Assert.Empty(cli.Calls);
    }

    [Fact]
    public async Task ErrorLevelLogEntry_DoesNotFlipAGreenRunToFailed() {
        // The documented trap: a successful workflow may log at Error level as observability.
        var (sut, _, _, jobs) = CreateSut("""
            {"Result":"Success","Data":{"output":"Session ended","errors":[],"logEntries":[{"source":"Debug","level":"Error","message":"retrying"},{"source":"Debug","level":"Critical","message":"still fine"}]}}
            """, execution: true);

        var result = await BackgroundJobs.AwaitFinished(jobs, await sut.RunWorkflow(CliToolFixtures.ProjectPath, "Main.xaml", includeLogEntries: true));

        Assert.Equal("success", result.Status);
        var payload = CliToolFixtures.Data<RunPayload>(result)!;
        Assert.True(payload.Succeeded);
        Assert.Equal(2, payload.LogEntries.Count);
        Assert.Contains(payload.LogEntries, l => l.Level == "Error");
        Assert.Contains("never a failure signal", payload.VerdictNote, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunFailure_IsReportedAsErrorWithTheReason() {
        var (sut, _, _, jobs) = CreateSut(NestedFailure, execution: true);

        var result = await BackgroundJobs.AwaitFinished(jobs, await sut.RunWorkflow(CliToolFixtures.ProjectPath, "Main.xaml"));

        Assert.Equal("error", result.Status);
        Assert.False(CliToolFixtures.Data<RunPayload>(result)!.Succeeded);
        Assert.Contains(result.Errors, e => e.Contains("boom", StringComparison.Ordinal));
        Assert.Contains(result.Errors, e => e.Contains("hasErrors=true", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FlatShapeFailureFromErrors_IsReportedAsError() {
        var (sut, _, _, jobs) = CreateSut("""
            {"Result":"Success","Data":{"output":"Execution aborted. See attached errors for more information","errors":[{"errorName":"System.InvalidOperationException","errorMessage":"boom","lineNumber":12}]}}
            """, execution: true);

        var result = await BackgroundJobs.AwaitFinished(jobs, await sut.RunWorkflow(CliToolFixtures.ProjectPath, "Main.xaml"));

        Assert.Equal("error", result.Status);
        var data = CliToolFixtures.Data<RunPayload>(result)!;
        Assert.False(data.Succeeded);
        Assert.Equal(12, data.Errors[0].LineNumber);
    }

    [Fact]
    public async Task MissingEntryPoint_FailsOnOutputWithEmptyErrors() {
        var (sut, _, _, jobs) = CreateSut("""
            {"Result":"Success","Data":{"output":"Failed to open the file C:\\proj\\Nope.xaml","errors":[]}}
            """, execution: true);

        var result = await BackgroundJobs.AwaitFinished(jobs, await sut.RunWorkflow(CliToolFixtures.ProjectPath, "Main.xaml"));

        Assert.Equal("error", result.Status);
        Assert.Contains(result.Errors, e => e.Contains("Failed to open the file", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LogEntriesAreOmittedByDefault_WithAWarning() {
        var (sut, _, _, jobs) = CreateSut(FlatSuccess, execution: true);

        var result = await BackgroundJobs.AwaitFinished(jobs, await sut.RunWorkflow(CliToolFixtures.ProjectPath, "Main.xaml"));

        Assert.Empty(CliToolFixtures.Data<RunPayload>(result)!.LogEntries);
        Assert.Contains(result.Warnings, w => w.Contains("includeLogEntries=true", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FailureEnvelope_ReturnsStructuredError() {
        var (sut, _, _, jobs) = CreateSut("""{"Result":"Failure","Message":"The project directory could not be opened."}""", execution: true);

        var result = await BackgroundJobs.AwaitFinished(jobs, await sut.RunWorkflow(CliToolFixtures.ProjectPath, "Main.xaml"));

        Assert.Equal("error", result.Status);
        Assert.Contains(result.Errors, e => e.Contains("could not be opened", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnparseablePayload_ReturnsCliUnparseableResponse() {
        var (sut, _, _, jobs) = CreateSut("not json", execution: true);

        var result = await BackgroundJobs.AwaitFinished(jobs, await sut.RunWorkflow(CliToolFixtures.ProjectPath, "Main.xaml"));

        Assert.Equal("error", result.Status);
        Assert.Contains(result.ErrorDetails, e => e.ErrorCode == "CLI_UNPARSEABLE_RESPONSE");
    }

    [Fact]
    public async Task CliMissingFromPath_ReportsCliUnavailable() {
        var cli = new RecordingStructuredCli { CliSuccess = false, ExitCode = -1, StdOut = string.Empty };
        cli.CliErrors.Add("The UiPath CLI ('uip') was not found on PATH (searched for uip.exe).");
        var filesystem = CliToolFixtures.ProjectOnlyFilesystem()
            .WithFile(Path.Combine(CliToolFixtures.ProjectPath, "Main.xaml"));
        var jobs = BackgroundJobs.NewStore();
        var sut = new RunWorkflowTool(cli, filesystem, CliToolFixtures.Policy(execution: true), jobs);

        var result = await BackgroundJobs.AwaitFinished(jobs, await sut.RunWorkflow(CliToolFixtures.ProjectPath, "Main.xaml"));

        Assert.Equal("error", result.Status);
        Assert.Contains(result.ErrorDetails, e => e.ErrorCode == "CLI_UNAVAILABLE");
    }

    [Fact]
    public async Task ProjectDirectoryIsTheWorkingDirectory() {
        var (sut, cli, _, jobs) = CreateSut(FlatSuccess, execution: true);

        await BackgroundJobs.AwaitFinished(jobs, await sut.RunWorkflow(CliToolFixtures.ProjectPath, "Main.xaml"));

        Assert.Equal(CliToolFixtures.ProjectPath, cli.WorkingDirectories[^1]);
    }

    [Fact]
    public async Task TimeoutIsClampedAndForwarded() {
        var (sut, cli, _, jobs) = CreateSut(FlatSuccess, execution: true);

        await BackgroundJobs.AwaitFinished(jobs, await sut.RunWorkflow(CliToolFixtures.ProjectPath, "Main.xaml", timeoutSeconds: 999999));

        Assert.Equal(CliToolSupport.MaxTimeoutSeconds, cli.Timeouts[^1]);
    }

    [Fact]
    public async Task MissingProjectJson_IsRefusedBeforeTheExecutionGate() {
        var cli = new RecordingStructuredCli();
        var jobs = BackgroundJobs.NewStore();
        var sut = new RunWorkflowTool(cli, new FakeFilesystemProvider { ProjectJson = null }, CliToolFixtures.Policy(execution: true), jobs);

        var result = await BackgroundJobs.AwaitFinished(jobs, await sut.RunWorkflow(CliToolFixtures.ProjectPath, "Main.xaml"));

        Assert.Equal("error", result.Status);
        Assert.Contains(result.ErrorDetails, e => e.ErrorCode == "PROJECT_JSON_NOT_FOUND");
    }

    private sealed record RunLogEntry(string? Source, string? Level, string? Message);

    private sealed record RunError(string? ErrorName, string? ErrorMessage, int? LineNumber);

    private sealed record RunPayload(
        bool Succeeded, string? EnvelopeResult, int ExitCode, bool? HasErrors, string? ErrorMessage,
        string? Output, List<RunError> Errors, string? DebugState, object? DebugDetails,
        string? ProfilingOutputDirectory, bool AwaitingDecision, string PayloadShape, string Command,
        List<RunLogEntry> LogEntries, bool LogEntriesTruncated, string VerdictNote);
}




