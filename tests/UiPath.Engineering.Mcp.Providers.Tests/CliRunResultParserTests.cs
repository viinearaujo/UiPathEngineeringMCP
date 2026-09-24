using UiPath.Engineering.Mcp.Providers.UiPathCli;

namespace UiPath.Engineering.Mcp.Providers.Tests;

public class CliRunResultParserTests {
    // The nested shape: Data.runResult is a JSON STRING that must be parsed separately.
    private static string Nested(string runResultJson) =>
        "{\"Result\":\"Success\",\"Code\":\"ToolResult\",\"Data\":{\"runResult\":"
        + Quote(runResultJson) + "}}";

    private static string Quote(string json) =>
        "\"" + json.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    [Fact]
    public void Parse_NestedRunResult_ReadsHasErrorsAndOutput() {
        var result = CliRunResultParser.Parse(Nested("""{"output":"{\"resultCode\":\"OK\"}","hasErrors":false,"errorMessage":null,"debugState":"Completed"}"""));

        Assert.NotNull(result);
        Assert.True(result!.UsedRunResultEnvelope);
        Assert.False(result.HasErrors);
        Assert.Equal("Completed", result.DebugState);
        Assert.True(result.Succeeded);
        Assert.Contains("resultCode", result.Output);
    }

    [Fact]
    public void Parse_NestedRunResult_HasErrorsTrue_IsFailure() {
        var result = CliRunResultParser.Parse(Nested("""{"output":"","hasErrors":true,"errorMessage":"Source: HttpRequest_1\nMessage: boom","debugState":"Completed"}"""));

        Assert.False(result!.Succeeded);
        Assert.Contains("boom", result.ErrorMessage);
        Assert.Equal("failure", VerdictOf(result));
    }

    [Fact]
    public void Parse_NestedRunResult_ProfilingOutputDirectory_IsSurfaced() {
        var result = CliRunResultParser.Parse(Nested(
            """{"output":"","hasErrors":false,"profiling":{"outputDirectory":"C:\\runs\\142305_Main"},"debugState":"Completed"}"""));

        Assert.Equal(@"C:\runs\142305_Main", result!.ProfilingOutputDirectory);
    }

    [Fact]
    public void Parse_NestedRunResult_ProfilingAbsent_LeavesItNull() {
        var result = CliRunResultParser.Parse(Nested("""{"output":"","hasErrors":false,"profiling":null}"""));

        Assert.Null(result!.ProfilingOutputDirectory);
    }

    [Fact]
    public void Parse_SuspendedSession_HasErrorsFalseButIsNotASuccess() {
        // The documented trap: a Suspended session has an awaiting exception while HasErrors is
        // still false, so DebugState must be checked before HasErrors.
        var result = CliRunResultParser.Parse(Nested(
            """{"output":"","hasErrors":false,"errorMessage":"Execution suspended on an unhandled exception; the session is still alive.","debugState":"Suspended"}"""));

        Assert.False(result!.HasErrors);
        Assert.True(result.IsSuspended);
        Assert.True(result.IsAwaitingDecision);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public void Parse_PausedSession_IsAwaitingDecision() {
        var result = CliRunResultParser.Parse(Nested(
            """{"output":"","hasErrors":false,"debugState":"Paused","debugDetails":"{\"Activity\":\"Assign x\",\"ActivityId\":\"1.5\"}"}"""));

        Assert.True(result!.IsPaused);
        Assert.True(result.IsAwaitingDecision);
        Assert.False(result.Succeeded);
        Assert.Equal("Assign x", result.DebugDetails!.Value.GetProperty("Activity").GetString());
    }

    [Fact]
    public void Parse_RunningSession_IsAwaitingDecision() {
        var result = CliRunResultParser.Parse(Nested("""{"output":"","hasErrors":false,"debugState":"Running"}"""));

        Assert.True(result!.IsRunning);
        Assert.True(result.IsAwaitingDecision);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public void Parse_PlainRun_HasNoDebugState() {
        var result = CliRunResultParser.Parse(Nested("""{"output":"{}","hasErrors":false,"debugState":null}"""));

        Assert.Null(result!.DebugState);
        Assert.False(result.IsAwaitingDecision);
        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Parse_FlatShape_SessionEndedWithNoErrors_IsSuccess() {
        const string stdOut = """
            {"Result":"Success","Code":"ToolResult","Data":{"output":"Session ended","errors":[],"logEntries":[{"source":"Debug","level":"Information","message":"5 + 5 = 10"}]}}
            """;

        var result = CliRunResultParser.Parse(stdOut);

        Assert.False(result!.UsedRunResultEnvelope);
        Assert.Null(result.HasErrors);
        Assert.True(result.Succeeded);
        var log = Assert.Single(result.LogEntries);
        Assert.Equal("5 + 5 = 10", log.Message);
    }

    [Fact]
    public void Parse_FlatShape_ErrorLevelLogEntry_DoesNotFlipTheVerdict() {
        // A successful workflow may log at Error level as observability; that is workflow data.
        const string stdOut = """
            {"Result":"Success","Data":{"output":"Session ended","errors":[],"logEntries":[{"source":"Debug","level":"Error","message":"retrying after transient fault"},{"source":"Debug","level":"Critical","message":"still fine"}]}}
            """;

        var result = CliRunResultParser.Parse(stdOut);

        Assert.True(result!.Succeeded);
        Assert.Equal(2, result.LogEntries.Count);
    }

    [Fact]
    public void Parse_FlatShape_PopulatedErrors_IsFailure() {
        const string stdOut = """
            {"Result":"Success","Data":{"output":"Execution aborted. See attached errors for more information","errors":[{"errorName":"System.InvalidOperationException","errorMessage":"boom","lineNumber":12}],"logEntries":[]}}
            """;

        var result = CliRunResultParser.Parse(stdOut);

        Assert.False(result!.Succeeded);
        var error = Assert.Single(result.Errors);
        Assert.Equal("System.InvalidOperationException", error.ErrorName);
        Assert.Equal(12, error.LineNumber);
    }

    [Fact]
    public void Parse_FlatShape_StyleDiagnosticInErrors_IsStillAFailure() {
        // A style/analyzer diagnostic (e.g. IDE0063) lands in errors and aborts the run.
        const string stdOut = """
            {"Result":"Success","Data":{"output":"Execution aborted. See attached errors for more information","errors":[{"errorName":"IDE0063","errorMessage":"'using' statement can be simplified."}]}}
            """;

        var result = CliRunResultParser.Parse(stdOut);

        Assert.False(result!.Succeeded);
        Assert.Equal("IDE0063", Assert.Single(result.Errors).ErrorName);
    }

    [Fact]
    public void Parse_FlatShape_MissingEntryPoint_FailsOnOutputWithEmptyErrors() {
        const string stdOut = """
            {"Result":"Success","Data":{"output":"Failed to open the file C:\\proj\\Nope.xaml","errors":[]}}
            """;

        var result = CliRunResultParser.Parse(stdOut);

        Assert.False(result!.Succeeded);
        Assert.Contains("Failed to open the file", result.Output);
    }

    [Fact]
    public void Parse_FlatShape_MidSessionEmptyOutput_IsSuccess() {
        // A stepping command reports an empty output; that is not a failed terminal status.
        const string stdOut = """
            {"Result":"Success","Data":{"output":"","hasErrors":false,"debugState":"Completed"}}
            """;

        Assert.True(CliRunResultParser.Parse(stdOut)!.Succeeded);
    }

    [Fact]
    public void Parse_FlatShape_DebugStateNone_IsSuccess() {
        // The live `debug state` probe with no session: {"output":"","hasErrors":false,"debugState":"None"}.
        const string stdOut = """
            {"Result":"Success","Code":"ToolResult","Data":{"output":"","hasErrors":false,"errorMessage":null,"profiling":null,"debugState":"None","debugDetails":null}}
            """;

        var result = CliRunResultParser.Parse(stdOut);

        Assert.Equal("None", result!.DebugState);
        Assert.False(result.IsAwaitingDecision);
        Assert.True(result.Succeeded);
        Assert.Null(result.DebugDetails);
    }

    [Fact]
    public void Parse_FailureEnvelope_IsFailure_EvenWhenHasErrorsIsFalse() {
        const string stdOut = """
            {"Result":"Failure","Message":"The project directory could not be opened.","Data":{"output":"","hasErrors":false}}
            """;

        var result = CliRunResultParser.Parse(stdOut);

        Assert.False(result!.EnvelopeSuccess);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public void Parse_NonEnvelopePayload_ReturnsNull() {
        Assert.Null(CliRunResultParser.Parse("not json"));
        Assert.Null(CliRunResultParser.Parse(""));
    }

    [Fact]
    public void Parse_BannerTextBeforePayload_StillReadsTheVerdict() {
        const string stdOut = """
            Checking for updates.
            {"Result":"Success","Data":{"output":"Session ended","errors":[]}}
            """;

        Assert.True(CliRunResultParser.Parse(stdOut)!.Succeeded);
    }

    [Fact]
    public void Parse_MalformedRunResultString_FallsBackToTheFlatRead() {
        const string stdOut = """{"Result":"Success","Data":{"runResult":"{not-json","output":"Session ended","errors":[]}}""";

        var result = CliRunResultParser.Parse(stdOut);

        Assert.False(result!.UsedRunResultEnvelope);
        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Parse_LargeLogEntries_AreCappedWithATruncationFlag() {
        var entries = string.Join(",", Enumerable.Range(0, 260).Select(i => "{\"source\":\"Debug\",\"level\":\"Trace\",\"message\":\"m" + i + "\"}"));
        var stdOut = "{\"Result\":\"Success\",\"Data\":{\"output\":\"Session ended\",\"errors\":[],\"logEntries\":[" + entries + "]}}";

        var result = CliRunResultParser.Parse(stdOut);

        Assert.Equal(200, result!.LogEntries.Count);
        Assert.True(result.LogEntriesTruncated);
    }

    [Fact]
    public void Parse_PascalCaseFields_AreReadToo() {
        var result = CliRunResultParser.Parse(Nested(
            """{"Output":"{}","HasErrors":false,"DebugState":"Completed","Profiling":{"OutputDirectory":"C:\\runs\\pascal"}}"""));

        Assert.True(result!.Succeeded);
        Assert.Equal("Completed", result.DebugState);
        Assert.Equal(@"C:\runs\pascal", result.ProfilingOutputDirectory);
    }

    private static string VerdictOf(CliRunResult result) => result.Succeeded ? "success" : "failure";
}
