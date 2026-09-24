using UiPath.Engineering.Mcp.Core.Configuration;
using UiPath.Engineering.Mcp.Providers.UiPathCli;

namespace UiPath.Engineering.Mcp.Providers.Tests;

public class CliCommandPolicyExecutionTests {
    private static CliCommandPolicy CreateSut(Action<UiPathCliOptions>? configure = null) {
        var options = new UiPathCliOptions();
        configure?.Invoke(options);
        return new CliCommandPolicy(options);
    }

    [Theory]
    [InlineData("run --file-path Main.xaml --output json")]
    [InlineData("debug start --file-path Main.xaml")]
    [InlineData("debug state --wait-timeout-seconds 0")]
    [InlineData("debug continue")]
    [InlineData("execution cancel")]
    public void IsExecution_RecognizesEveryExecutionVerb(string arguments) {
        Assert.True(CreateSut().IsExecution("rpa", arguments));
    }

    [Theory]
    [InlineData("validate --project-dir x")]
    [InlineData("build x")]
    [InlineData("analyzer-rules list --scope Workflow")]
    [InlineData("packages versions --package-id P")]
    [InlineData("packages inspect --package-name P")]
    [InlineData("get-object-repository --project-dir x")]
    [InlineData("get-library-object-repository --library-paths a.nupkg")]
    public void IsExecution_DoesNotMatchReadOrBuildVerbs(string arguments) {
        Assert.False(CreateSut().IsExecution("rpa", arguments));
    }

    [Fact]
    public void IsExecution_PrefixMatchDoesNotMatchLongerToken() {
        var sut = CreateSut();

        // "runner" and "debugger" must not prefix-match "run" / "debug".
        Assert.False(sut.IsExecution("rpa", "runner --file-path Main.xaml"));
        Assert.False(sut.IsExecution("rpa", "debugger start"));
    }

    [Fact]
    public void IsExecution_VerbOutsideAllowlist_IsFalse() {
        Assert.False(CreateSut().IsExecution("orx", "run --file-path Main.xaml"));
    }

    [Fact]
    public void IsExecution_RejectedCharacters_IsFalse() {
        Assert.False(CreateSut().IsExecution("rpa", "run --file-path Main.xaml\nwhoami"));
    }

    [Fact]
    public void ExecutionEnabled_FollowsTheConfigFlag_AndDefaultsFalse() {
        Assert.False(CreateSut().ExecutionEnabled);
        Assert.True(CreateSut(o => o.EnableExecution = true).ExecutionEnabled);
    }

    [Fact]
    public void MutatingEnabled_FollowsTheConfigFlag_AndDefaultsFalse() {
        Assert.False(CreateSut().MutatingEnabled);
        Assert.True(CreateSut(o => o.EnableMutatingCommands = true).MutatingEnabled);
    }

    [Fact]
    public void ExecutionVerbs_AreNeverClassifiedReadOnly() {
        // Fail closed: run/debug/execution stay mutating for the run_ui_path_cli hatch, so they
        // need EnableMutatingCommands there and EnableExecution on the dedicated tools.
        var sut = CreateSut(o => o.EnableMutatingCommands = true);

        Assert.Equal(CliCommandClass.AllowedMutating, sut.Classify("rpa", "run --file-path Main.xaml"));
        Assert.Equal(CliCommandClass.AllowedMutating, sut.Classify("rpa", "debug start --file-path Main.xaml"));
        Assert.Equal(CliCommandClass.AllowedMutating, sut.Classify("rpa", "execution cancel"));
    }

    [Theory]
    [InlineData("analyzer-rules list --scope Workflow --project-dir x --output json")]
    [InlineData("analyzer-rules list --project-dir x --output json")]
    [InlineData("packages versions --package-id P --include-prerelease --output json")]
    [InlineData("packages inspect --package-name P --output json")]
    [InlineData("get-object-repository --project-dir x --output json")]
    [InlineData("get-library-object-repository --library-paths a.nupkg --output json")]
    public void NewReadOnlySubcommands_AreAllowedWithoutEnableMutatingCommands(string arguments) {
        Assert.Equal(CliCommandClass.AllowedReadOnly, CreateSut().Classify("rpa", arguments));
    }

    [Fact]
    public void PackagesInstall_IsMutatingByDefault() {
        Assert.Equal(
            CliCommandClass.AllowedMutating,
            CreateSut().Classify("rpa", "packages install --packages id=P --output json"));
    }

    [Fact]
    public void AnalyzerRulesSubcommandPrefixMatch_DoesNotMatchLongerToken() {
        // "analyzer-rules listing" must not prefix-match "analyzer-rules list".
        Assert.Equal(
            CliCommandClass.AllowedMutating,
            CreateSut().Classify("rpa", "analyzer-rules listing --output json"));
    }
}

public class CliVerbArgumentsTests {
    private const string Project = @"C:\projects\testProcess";

    [Fact]
    public void AnalyzerRulesList_ScopesTheCall() {
        Assert.Equal(
            ["analyzer-rules", "list", "--scope", "Coded Workflow", "--project-dir", Project, "--output", "json"],
            CliVerbArguments.AnalyzerRulesList(Project, "Coded Workflow"));
    }

    [Fact]
    public void AnalyzerRulesUnscoped_OmitsTheScopeFlag() {
        Assert.Equal(
            ["analyzer-rules", "list", "--project-dir", Project, "--output", "json"],
            CliVerbArguments.AnalyzerRulesUnscoped(Project));
    }

    [Fact]
    public void PackagesVersions_IncludePrereleaseByDefault() {
        Assert.Equal(
            ["packages", "versions", "--package-id", "UiPath.Excel.Activities", "--include-prerelease", "--project-dir", Project, "--output", "json"],
            CliVerbArguments.PackagesVersions(Project, "UiPath.Excel.Activities", includePrerelease: true));
    }

    [Fact]
    public void PackagesVersions_CanOmitPrerelease() {
        Assert.Equal(
            ["packages", "versions", "--package-id", "P", "--project-dir", Project, "--output", "json"],
            CliVerbArguments.PackagesVersions(Project, "P", includePrerelease: false));
    }

    [Fact]
    public void PackagesInstall_OneOccurrencePerPackage() {
        Assert.Equal(
            [
                "packages",
                "install",
                "--packages",
                "id=UiPath.System.Activities,version=23.10.1",
                "--packages",
                "id=UiPath.Excel.Activities",
                "--project-dir",
                Project,
                "--output",
                "json"
            ],
            CliVerbArguments.PackagesInstall(Project, ["id=UiPath.System.Activities,version=23.10.1", "id=UiPath.Excel.Activities"]));
    }

    [Fact]
    public void PackagesInspect_PassesOnlySuppliedFlags() {
        Assert.Equal(
            ["packages", "inspect", "--package-name", "P", "--project-dir", Project, "--output", "json"],
            CliVerbArguments.PackagesInspect(Project, "P", null, null, null));

        Assert.Equal(
            ["packages", "inspect", "--nupkg-path", @"C:\libs\P.1.0.0.nupkg", "--project-dir", Project, "--output", "json"],
            CliVerbArguments.PackagesInspect(Project, null, null, null, @"C:\libs\P.1.0.0.nupkg"));
    }

    [Fact]
    public void ObjectRepositoryGet_TakesOnlyTheProjectDir() {
        Assert.Equal(
            ["get-object-repository", "--project-dir", Project, "--output", "json"],
            CliVerbArguments.ObjectRepositoryGet(Project));
    }

    [Fact]
    public void ObjectRepositoryGetLibrary_PassesOneCommaSeparatedFlag() {
        // It is NOT a repeatable flag: one --library-paths with comma-separated absolute paths.
        var paths = @"C:\libs\Acme.UiLib.1.2.0.nupkg,C:\libs\Other.UiLib.2.0.0.nupkg";

        Assert.Equal(
            ["get-library-object-repository", "--library-paths", paths, "--project-dir", Project, "--output", "json"],
            CliVerbArguments.ObjectRepositoryGetLibrary(Project, paths));
    }

    [Fact]
    public void Run_PassesFilePathAndRepeatableInputArguments() {
        Assert.Equal(
            [
                "run",
                "--file-path",
                "Main.xaml",
                "--input-arguments",
                "name=John",
                "--input-arguments",
                "retries:=3",
                "--project-dir",
                Project,
                "--output",
                "json"
            ],
            CliVerbArguments.Run(Project, "Main.xaml", ["name=John", "retries:=3"]));
    }

    [Fact]
    public void Run_SkipBuildAndProfiling_AreFlagsWithoutValues() {
        Assert.Equal(
            [
                "run",
                "--file-path",
                "Main.xaml",
                "--skip-build",
                "--profiling",
                "--profiling-mode",
                "stream",
                "--project-dir",
                Project,
                "--output",
                "json"
            ],
            CliVerbArguments.Run(Project, "Main.xaml", null, null, skipBuild: true, profiling: true, profilingMode: "stream"));
    }

    [Fact]
    public void Run_LogLevelIsPassedThrough() {
        var tokens = CliVerbArguments.Run(Project, "Main.xaml", null, logLevel: "Warning");

        Assert.Contains("--log-level", tokens);
        Assert.Equal("Warning", tokens[Array.IndexOf(tokens, "--log-level") + 1]);
    }

    [Fact]
    public void DebugStart_PassesBreakpointsAsRepeatableItems() {
        Assert.Equal(
            [
                "debug",
                "start",
                "--file-path",
                "Main.xaml",
                "--breakpoints",
                "workflowFile=Main.xaml,activityIdRef=Assign_1",
                "--breakpoints",
                "workflowFile=Main.xaml,activityIdRef=Click_2,hitCount:=3",
                "--project-dir",
                Project,
                "--output",
                "json"
            ],
            CliVerbArguments.DebugStart(Project, "Main.xaml", null,
                ["workflowFile=Main.xaml,activityIdRef=Assign_1", "workflowFile=Main.xaml,activityIdRef=Click_2,hitCount:=3"]));
    }

    [Fact]
    public void DebugCommand_PassesWaitTimeout() {
        Assert.Equal(
            ["debug", "state", "--wait-timeout-seconds", "0", "--project-dir", Project, "--output", "json"],
            CliVerbArguments.DebugCommand(Project, "state", waitTimeoutSeconds: 0));

        Assert.Equal(
            ["debug", "step-over", "--project-dir", Project, "--output", "json"],
            CliVerbArguments.DebugCommand(Project, "step-over"));
    }

    [Fact]
    public void DebugSetBreakpoints_ReplacesTheWholeSet() {
        Assert.Equal(
            [
                "debug",
                "set-breakpoints",
                "--breakpoints",
                "workflowFile=Main.xaml,activityIdRef=A_1",
                "--project-dir",
                Project,
                "--output",
                "json"
            ],
            CliVerbArguments.DebugSetBreakpoints(Project, ["workflowFile=Main.xaml,activityIdRef=A_1"]));
    }

    [Fact]
    public void ExecutionCancel_EndsRunAndDebugSessions() {
        Assert.Equal(
            ["execution", "cancel", "--project-dir", Project, "--output", "json"],
            CliVerbArguments.ExecutionCancel(Project));
    }

    [Fact]
    public void WithRpaVerb_PrependsTheTopLevelVerb() {
        Assert.Equal(
            ["rpa", "run", "--file-path", "Main.xaml"],
            CliVerbArguments.WithRpaVerb(["run", "--file-path", "Main.xaml"]));
    }

    [Fact]
    public void ToArgumentString_QuotesOnlyTokensThatNeedGrouping() {
        var tokens = new[] { "rpa", "run", "--file-path", "My Workflow.xaml", "--project-dir", Project };

        Assert.Equal(
            "rpa run --file-path \"My Workflow.xaml\" --project-dir " + Project,
            CliVerbArguments.ToArgumentString(tokens));
    }

    [Fact]
    public void ToArgumentString_QuotesAnEmptyToken() {
        Assert.Equal("a \"\" b", CliVerbArguments.ToArgumentString(["a", "", "b"]));
    }

    [Fact]
    public void ToArgumentString_RoundTripsThroughTheProviderTokenizer() {
        // The rendered string must tokenize back to the original tokens, since RunAsync splits it.
        var tokens = new[] { "rpa", "run", "--file-path", "My Workflow.xaml", "--input-arguments", "message=Hello, world!", "--project-dir", Project };

        Assert.Equal(tokens, ProcessRunnerSplit(CliVerbArguments.ToArgumentString(tokens)));
    }

    [Theory]
    [InlineData("key=value with spaces")]
    [InlineData(@"C:\path with spaces\proj")]
    public void ToArgumentString_TokenWithSpacesSurvivesAsOneToken(string value) {
        var tokens = new[] { "rpa", "run", "--file-path", value };

        var roundTripped = ProcessRunnerSplit(CliVerbArguments.ToArgumentString(tokens));

        Assert.Equal(tokens, roundTripped);
    }

    [Fact]
    public void IsUnpassableInline_FlagsQuotesAndControlCharacters() {
        Assert.True(CliVerbArguments.IsUnpassableInline("say \"hi\""));
        Assert.True(CliVerbArguments.IsUnpassableInline("line1\nline2"));
        Assert.True(CliVerbArguments.IsUnpassableInline("with\0nul"));
        Assert.False(CliVerbArguments.IsUnpassableInline("message=Hello, world!"));
        Assert.False(CliVerbArguments.IsUnpassableInline("plain-value"));
    }

    [Fact]
    public void DebugSessionCommands_ExcludesStartAndSetBreakpoints() {
        Assert.DoesNotContain("start", CliVerbArguments.DebugSessionCommands);
        Assert.DoesNotContain("set-breakpoints", CliVerbArguments.DebugSessionCommands);
        Assert.DoesNotContain("cancel", CliVerbArguments.DebugSessionCommands);
        Assert.Contains("state", CliVerbArguments.DebugSessionCommands);
        Assert.Contains("continue-retry", CliVerbArguments.DebugSessionCommands);
        Assert.Equal("cancel", CliVerbArguments.CancelCommand);
    }

    [Fact]
    public void AnalyzerRuleScopes_MatchTheLiveCliAcceptedValues() {
        Assert.Equal(["Activity", "Workflow", "Project", "Coded Workflow"], CliVerbArguments.AnalyzerRuleScopes);
    }

    // ProcessRunner is internal to the Providers assembly; the tests project sees it via
    // InternalsVisibleTo, so tokenize exactly the way the provider will.
    private static List<string> ProcessRunnerSplit(string arguments) =>
        ProcessRunner.SplitQuotedArguments(arguments);
}
