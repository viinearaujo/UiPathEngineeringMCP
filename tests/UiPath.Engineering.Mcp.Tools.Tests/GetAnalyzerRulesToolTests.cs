using UiPath.Engineering.Mcp.Core.Models;
using UiPath.Engineering.Mcp.Tools;

namespace UiPath.Engineering.Mcp.Tools.Tests;

public class GetAnalyzerRulesToolTests {
    private const string WorkflowScopePayload = """
        {
          "Result": "Success",
          "Code": "ToolResult",
          "Data": {
            "message": "Found 3 enabled rule(s):\n\n# Workflow\n[error] ST-SEC-008 (Workflow) - SecureString Variable Usage\n  recommendation: Use the Type Secure Text activity to log in.\n  parameters: VariableDepthUsage=1\n  docs: https://docs.uipath.com/en_US/studio/standalone/latest/user-guide/st-sec-008\n[warning] ST-NMG-002 (Workflow) - Arguments Naming Convention\n  recommendation: Make sure all the arguments follow the naming convention.\n  docs: https://docs.uipath.com/en_US/studio/standalone/latest/user-guide/st-nmg-002\n[info] ST-ANA-003 (Workflow) - Project Workflow Count\n  docs: https://docs.uipath.com/en_US/studio/standalone/latest/user-guide/st-ana-003"
          }
        }
        """;

    private static (GetAnalyzerRulesTool Sut, RecordingStructuredCli Cli, FakeFilesystemProvider Fs) CreateSut(
        string stdOut = WorkflowScopePayload) {
        var cli = new RecordingStructuredCli { StdOut = stdOut };
        var filesystem = CliToolFixtures.ProjectFilesystem();
        return (new GetAnalyzerRulesTool(cli, filesystem, CliToolFixtures.Policy()), cli, filesystem);
    }

    [Fact]
    public async Task DefaultsToWorkflowScope_AndRunsTheScopedVerb() {
        var (sut, cli, _) = CreateSut();

        var result = await sut.GetAnalyzerRules(CliToolFixtures.ProjectPath);

        Assert.Equal("success", result.Status);
        Assert.Equal(
            ["rpa", "analyzer-rules", "list", "--scope", "Workflow", "--project-dir", CliToolFixtures.ProjectPath, "--output", "json"],
            cli.LastTokens);
    }

    [Fact]
    public async Task ReturnsIdSeverityScopeTitleRecommendationAndDocs() {
        var (sut, _, _) = CreateSut();

        var result = await sut.GetAnalyzerRules(CliToolFixtures.ProjectPath);
        var data = CliToolFixtures.Data<AnalyzerRulesPayload>(result)!;

        Assert.Equal(3, data.ReturnedRules);
        var first = data.Rules[0];
        Assert.Equal("ST-SEC-008", first.Id);
        Assert.Equal("error", first.Severity);
        Assert.Equal("Workflow", first.Scope);
        Assert.Equal("SecureString Variable Usage", first.Title);
        Assert.Equal("Use the Type Secure Text activity to log in.", first.Recommendation);
        Assert.Equal("https://docs.uipath.com/en_US/studio/standalone/latest/user-guide/st-sec-008", first.Docs);
        Assert.Equal("studio-builtin", first.Source);
    }

    [Fact]
    public async Task ConfiguredParameters_AreReturned() {
        var (sut, _, _) = CreateSut();

        var result = await sut.GetAnalyzerRules(CliToolFixtures.ProjectPath);
        var data = CliToolFixtures.Data<AnalyzerRulesPayload>(result)!;

        Assert.Equal("1", data.Rules[0].Parameters["VariableDepthUsage"]);
    }

    [Theory]
    [InlineData("Activity")]
    [InlineData("Workflow")]
    [InlineData("Project")]
    [InlineData("Coded Workflow")]
    public async Task EveryDocumentedScope_IsAccepted(string scope) {
        var (sut, cli, _) = CreateSut();

        var result = await sut.GetAnalyzerRules(CliToolFixtures.ProjectPath, scope);

        Assert.Equal("success", result.Status);
        Assert.Equal(scope, CliToolFixtures.TokenAfter(cli.LastTokens, "--scope"));
    }

    [Fact]
    public async Task ScopeIsCaseInsensitive_AndNormalizedToTheDocumentedValue() {
        var (sut, cli, _) = CreateSut();

        var result = await sut.GetAnalyzerRules(CliToolFixtures.ProjectPath, "coded workflow");

        Assert.Equal("success", result.Status);
        Assert.Contains("Coded Workflow", cli.LastTokens!);
    }

    [Fact]
    public async Task UnknownScope_IsRefusedWithoutCallingTheCli() {
        var (sut, cli, _) = CreateSut();

        var result = await sut.GetAnalyzerRules(CliToolFixtures.ProjectPath, "Everything");

        Assert.Equal("error", result.Status);
        Assert.Contains(result.ErrorDetails, e => e.ErrorCode == "INVALID_ARGUMENT");
        Assert.Empty(cli.Calls);
    }

    [Fact]
    public async Task ScopeAll_OmitsTheScopeFlag_AndWarnsAboutTheCost() {
        var (sut, cli, _) = CreateSut();

        var result = await sut.GetAnalyzerRules(CliToolFixtures.ProjectPath, "All");

        Assert.Equal("success", result.Status);
        Assert.DoesNotContain("--scope", cli.LastTokens!);
        Assert.Contains(result.Warnings, w => w.Contains("minute or more", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MinSeverity_FiltersOutLowerSeverities() {
        var (sut, _, _) = CreateSut();

        var result = await sut.GetAnalyzerRules(CliToolFixtures.ProjectPath, minSeverity: "error");
        var data = CliToolFixtures.Data<AnalyzerRulesPayload>(result)!;

        Assert.Equal(3, data.TotalRules);
        Assert.Equal(1, data.ReturnedRules);
        Assert.Equal("ST-SEC-008", data.Rules[0].Id);
        Assert.Contains(result.Warnings, w => w.Contains("below severity", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MinSeverityWarning_KeepsErrorAndWarning() {
        var (sut, _, _) = CreateSut();

        var result = await sut.GetAnalyzerRules(CliToolFixtures.ProjectPath, minSeverity: "warning");

        Assert.Equal(2, CliToolFixtures.Data<AnalyzerRulesPayload>(result)!.ReturnedRules);
    }

    [Fact]
    public async Task UnknownMinSeverity_IsRefused() {
        var (sut, cli, _) = CreateSut();

        var result = await sut.GetAnalyzerRules(CliToolFixtures.ProjectPath, minSeverity: "critical");

        Assert.Equal("error", result.Status);
        Assert.Empty(cli.Calls);
    }

    [Fact]
    public async Task NoRulesReported_WarnsInsteadOfFailing() {
        var (sut, _, _) = CreateSut("""{"Result":"Success","Data":{"message":"No analyzer rules found."}}""");

        var result = await sut.GetAnalyzerRules(CliToolFixtures.ProjectPath);

        Assert.Equal("success", result.Status);
        Assert.Equal(0, CliToolFixtures.Data<AnalyzerRulesPayload>(result)!.TotalRules);
        Assert.Contains(result.Warnings, w => w.Contains("No analyzer rules", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FailureEnvelope_ReturnsStructuredErrorWithAFixHint() {
        var (sut, _, _) = CreateSut("""{"Result":"Failure","Message":"The project is not open in Studio."}""");

        var result = await sut.GetAnalyzerRules(CliToolFixtures.ProjectPath);

        Assert.Equal("error", result.Status);
        var error = Assert.Single(result.ErrorDetails);
        Assert.Equal("OPERATION_FAILED", error.ErrorCode);
        Assert.Contains("not open in Studio", error.Message);
        Assert.Equal("validate_project", error.SuggestedTool);
    }

    [Fact]
    public async Task UnparseablePayload_ReturnsCliUnparseableResponse() {
        var (sut, _, _) = CreateSut("not json at all");

        var result = await sut.GetAnalyzerRules(CliToolFixtures.ProjectPath);

        Assert.Equal("error", result.Status);
        Assert.Contains(result.ErrorDetails, e => e.ErrorCode == "CLI_UNPARSEABLE_RESPONSE");
    }

    [Fact]
    public async Task CliMissingFromPath_ReportsCliUnavailable() {
        var cli = new RecordingStructuredCli {
            CliSuccess = false,
            ExitCode = -1,
            StdOut = string.Empty
        };
        cli.CliErrors.Add("The UiPath CLI ('uip') was not found on PATH (searched for uip.exe).");
        var sut = new GetAnalyzerRulesTool(cli, CliToolFixtures.ProjectFilesystem(), CliToolFixtures.Policy());

        var result = await sut.GetAnalyzerRules(CliToolFixtures.ProjectPath);

        Assert.Equal("error", result.Status);
        Assert.Contains(result.ErrorDetails, e => e.ErrorCode == "CLI_UNAVAILABLE");
    }

    [Fact]
    public async Task MissingProjectJson_IsRefusedWithoutCallingTheCli() {
        var cli = new RecordingStructuredCli();
        var filesystem = new FakeFilesystemProvider { ProjectJson = null };
        var sut = new GetAnalyzerRulesTool(cli, filesystem, CliToolFixtures.Policy());

        var result = await sut.GetAnalyzerRules(CliToolFixtures.ProjectPath);

        Assert.Equal("error", result.Status);
        Assert.Contains(result.ErrorDetails, e => e.ErrorCode == "PROJECT_JSON_NOT_FOUND");
        Assert.Empty(cli.Calls);
    }

    [Fact]
    public async Task PathOutsideAllowedRoots_IsRefused() {
        var cli = new RecordingStructuredCli();
        var filesystem = new FakeFilesystemProvider { Allowed = false };
        var sut = new GetAnalyzerRulesTool(cli, filesystem, CliToolFixtures.Policy());

        var result = await sut.GetAnalyzerRules(CliToolFixtures.ProjectPath);

        Assert.Equal("error", result.Status);
        Assert.Contains(result.ErrorDetails, e => e.ErrorCode == "PATH_NOT_ALLOWED");
        Assert.Empty(cli.Calls);
    }

    [Fact]
    public async Task TimeoutIsClampedToTheConfiguredMaximum() {
        var (sut, cli, _) = CreateSut();

        await sut.GetAnalyzerRules(CliToolFixtures.ProjectPath, timeoutSeconds: 999999);

        Assert.Equal(CliToolSupport.MaxTimeoutSeconds, cli.Timeouts[^1]);
    }

    [Fact]
    public async Task ProjectDirectoryIsTheWorkingDirectory() {
        var (sut, cli, _) = CreateSut();

        await sut.GetAnalyzerRules(CliToolFixtures.ProjectPath);

        Assert.Equal(CliToolFixtures.ProjectPath, cli.WorkingDirectories[^1]);
    }

    [Fact]
    public async Task ReadOnlyVerb_NeedsNoEnableMutatingCommands() {
        // analyzer-rules list is in UiPathCli:ReadOnlySubcommands, so the default policy passes.
        var (sut, cli, _) = CreateSut();

        var result = await sut.GetAnalyzerRules(CliToolFixtures.ProjectPath);

        Assert.Equal("success", result.Status);
        Assert.Single(cli.Calls);
    }

    private sealed record RulePayload(
        string Id, string Severity, string Scope, string Title,
        string? Recommendation, string? Docs, Dictionary<string, string> Parameters, string Source);

    private sealed record AnalyzerRulesPayload(
        string Scope, string MinSeverity, int TotalRules, int ReturnedRules,
        string Command, List<RulePayload> Rules, string Note);
}
