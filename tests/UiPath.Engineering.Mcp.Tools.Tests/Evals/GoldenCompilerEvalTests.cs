using UiPath.Engineering.Mcp.Core;
using UiPath.Engineering.Mcp.Core.Authoring;

namespace UiPath.Engineering.Mcp.Tools.Tests.Evals;

/// <summary>
/// The structural golden evals. Each one builds a workflow with a real tool and
/// then PARSEs the emitted XAML to assert its shape — container body wrappers,
/// Rule 24 &lt;Sequence&gt; wraps on every slot, IdRef/HintSize on every activity,
/// ShapeLocation+ShapeSize on diagram nodes, annotation round-trip, and the
/// correct &lt;x:TypeArguments&gt; on Assign. This replaces the substring harness,
/// which passed a bare <c>&lt;ui:RetryScope&gt;</c> with no ActivityBody and an
/// unwrapped <c>If.Then</c> because neither difference is visible to
/// <c>xaml.Contains(token)</c>.
/// </summary>
public class GoldenCompilerEvalTests {
    private readonly ITestOutputHelper _output;

    public GoldenCompilerEvalTests(ITestOutputHelper output) => _output = output;

    [Fact]
    [Trait("Category", "GoldenEval")]
    public async Task ExcelForeach_SpecValidatesAndXamlEmits() {
        var outcome = await GoldenEvalTasks.ExcelForeach(new GoldenEvalContext());
        AssertPassed(outcome);
    }

    [Fact]
    [Trait("Category", "GoldenEval")]
    public async Task InvokeArgs_SpecValidatesAndXamlEmits() {
        var outcome = await GoldenEvalTasks.InvokeArgs(new GoldenEvalContext());
        AssertPassed(outcome);
    }

    [Fact]
    [Trait("Category", "GoldenEval")]
    public async Task TryCatchRetry_SpecValidatesAndXamlEmits() {
        var outcome = await GoldenEvalTasks.TryCatchRetry(new GoldenEvalContext());
        AssertPassed(outcome);
    }

    [Fact]
    [Trait("Category", "GoldenEval")]
    public async Task IfElse_SpecValidatesAndXamlEmits() {
        var outcome = await GoldenEvalTasks.IfElse(new GoldenEvalContext());
        AssertPassed(outcome);
    }

    [Fact]
    [Trait("Category", "GoldenEval")]
    public async Task Switch_SpecValidatesAndXamlEmits() {
        var outcome = await GoldenEvalTasks.Switch(new GoldenEvalContext());
        AssertPassed(outcome);
    }

    [Fact]
    [Trait("Category", "GoldenEval")]
    public async Task CodedHelper_WritesCsAndEmitsInvokeCode() {
        var outcome = await GoldenEvalTasks.CodedHelper(new GoldenEvalContext());
        AssertPassed(outcome);
    }

    [Fact]
    [Trait("Category", "GoldenEval")]
    public async Task BrokenInvokeFix_InvalidSpecThenFixedEmit() {
        var outcome = await GoldenEvalTasks.BrokenInvokeFix(new GoldenEvalContext());
        AssertPassed(outcome);
    }

    /// <summary>
    /// The flipped eval 08: the documented placeholder-selector UIA path must now
    /// SUCCEED, not be refused.
    /// </summary>
    [Fact]
    [Trait("Category", "GoldenEval")]
    public async Task UiaPlaceholderSelector_Succeeds() {
        var outcome = await GoldenEvalTasks.UiaPlaceholderSelectorSucceeds(new GoldenEvalContext());
        AssertPassed(outcome);
        Assert.True(outcome.XamlEmits);
    }

    /// <summary>
    /// The catalog guard must still refuse an activity that genuinely is not in
    /// the catalog — the UIA fix widened the catalog, it did not disable the guard.
    /// </summary>
    [Fact]
    [Trait("Category", "GoldenEval")]
    public async Task UnknownXamlWrite_IsRefused() {
        var outcome = await GoldenEvalTasks.UnknownXamlWriteRefused(new GoldenEvalContext());
        AssertPassed(outcome);
        Assert.False(outcome.UnknownXamlWriteSucceeded);
    }

    [Fact]
    [Trait("Category", "GoldenEval")]
    public async Task RecommendActivities_ReturnsExcelHits() {
        var outcome = await GoldenEvalTasks.RecommendActivitiesHits(new GoldenEvalContext());
        AssertPassed(outcome);
    }

    [Fact]
    [Trait("Category", "GoldenEval")]
    public async Task ValidateProject_MapsActivityIdAndSpecFix() {
        var outcome = await GoldenEvalTasks.ValidateProjectDiagnostics(new GoldenEvalContext());
        AssertPassed(outcome);
    }

    [Fact]
    [Trait("Category", "GoldenEval")]
    public async Task CodedIdiomGaps_FiresCodedTryLogAndXamlPreferCoded() {
        var outcome = await GoldenEvalTasks.CodedIdiomGaps(new GoldenEvalContext());
        AssertPassed(outcome);
    }

    [Fact]
    [Trait("Category", "GoldenEval")]
    public async Task AssignAnnotationArguments_TypedAssignMembersAndAnnotationRoundTrip() {
        var outcome = await GoldenEvalTasks.AssignAnnotationArguments(new GoldenEvalContext());
        AssertPassed(outcome);
    }

    [Fact]
    [Trait("Category", "GoldenEval")]
    public async Task ExpressionLanguage_VbAndCSharpBindingFormsFromOneSpec() {
        var outcome = await GoldenEvalTasks.ExpressionLanguage(new GoldenEvalContext());
        AssertPassed(outcome);
    }

    [Fact]
    [Trait("Category", "GoldenEval")]
    public async Task ReadabilityGaps_FiresReadabilityCategoryWithConfidence() {
        var outcome = await GoldenEvalTasks.ReadabilityGaps(new GoldenEvalContext());
        AssertPassed(outcome);
    }

    [Fact]
    [Trait("Category", "GoldenEval")]
    public async Task Diagrams_FlowchartAndStateMachineStructureFirst() {
        var outcome = await GoldenEvalTasks.Diagrams(new GoldenEvalContext());
        AssertPassed(outcome);
    }

    [Fact]
    [Trait("Category", "GoldenEval")]
    public async Task CodedFirstPath_KindsModesAndActivityIdStale() {
        var outcome = await GoldenEvalTasks.CodedFirstPath(new GoldenEvalContext());
        AssertPassed(outcome);
    }

    [Fact]
    [Trait("Category", "GoldenEval")]
    public async Task Scorecard_ReportsStructuralCoverage() {
        var outcomes = await GoldenEvalTasks.RunAll();
        foreach (var outcome in outcomes) {
            _output.WriteLine($"{(outcome.Passed ? "PASS" : "FAIL")}  {outcome.Id,-28} {outcome.Name,-44} {outcome.Detail}");
        }

        var specApplicable = outcomes.Where(o => o.SpecValidates.HasValue).ToList();
        var specPass = specApplicable.Count(o => o.SpecValidates == true);
        var xamlApplicable = outcomes.Where(o => o.XamlEmits.HasValue).ToList();
        var xamlPass = xamlApplicable.Count(o => o.XamlEmits == true);
        var escapeAttempts = outcomes.Where(o => o.UnknownXamlWriteSucceeded.HasValue).ToList();
        var escapeSuccesses = escapeAttempts.Count(o => o.UnknownXamlWriteSucceeded == true);
        var passed = outcomes.Count(o => o.Passed);
        var families = outcomes.SelectMany(o => o.Families).Distinct(StringComparer.Ordinal).OrderBy(f => f, StringComparer.Ordinal).ToList();

        _output.WriteLine("");
        _output.WriteLine($"tasks passed:            {passed}/{outcomes.Count}");
        _output.WriteLine($"spec validate pass:      {specPass}/{specApplicable.Count}");
        _output.WriteLine($"XAML emit pass:          {xamlPass}/{xamlApplicable.Count}");
        _output.WriteLine($"unexpected escape-hatch: {escapeSuccesses}/{escapeAttempts.Count} (target 0)");
        _output.WriteLine($"structural families:     {string.Join(", ", families)}");

        // Every eval passed, every spec validated, every XAML emitted a correct
        // shape, and the catalog guard let nothing unknown through. The escape rate
        // is target 0 for UNKNOWN activities; the UIA eval (08) is a documented,
        // in-catalog path and records UnknownXamlWriteSucceeded=false.
        Assert.Equal(17, outcomes.Count);
        Assert.Equal(outcomes.Count, passed);
        Assert.Equal(specApplicable.Count, specPass);
        Assert.Equal(xamlApplicable.Count, xamlPass);
        Assert.Equal(0, escapeSuccesses);

        // The structural families the plan mandates must all be covered by at least
        // one eval.
        foreach (var required in new[] {
            "body-shape", "rule24", "idref", "viewstate", "diagram", "annotation",
            "type-arguments", "expression-language", "uia", "readability", "coded-first", "catalog-guard"
        }) {
            Assert.True(families.Contains(required, StringComparer.Ordinal),
                $"the scorecard covers no eval in the '{required}' family");
        }

        RecordScorecard(outcomes, passed, specApplicable, specPass, xamlApplicable, xamlPass, escapeAttempts, escapeSuccesses, families);
    }

    // A recorded scorecard, so results stop living only in ephemeral CI trx
    // artifacts. Written next to the test assembly and committed under
    // tests/**/Evals/scorecards/.
    private static void RecordScorecard(
        IReadOnlyList<EvalOutcome> outcomes,
        int passed,
        IReadOnlyList<EvalOutcome> specApplicable,
        int specPass,
        IReadOnlyList<EvalOutcome> xamlApplicable,
        int xamlPass,
        IReadOnlyList<EvalOutcome> escapeAttempts,
        int escapeSuccesses,
        IReadOnlyList<string> families) {
        var builder = new System.Text.StringBuilder();
        builder.AppendLine("# Golden eval scorecard");
        builder.AppendLine();
        builder.AppendLine("Generated by `GoldenCompilerEvalTests.Scorecard_ReportsStructuralCoverage`.");
        builder.AppendLine("Everything below is produced by assertions that PARSE the emitted XAML.");
        builder.AppendLine();
        builder.AppendLine("| eval | name | result | structural families | detail |");
        builder.AppendLine("|---|---|---|---|---|");
        foreach (var outcome in outcomes) {
            builder.AppendLine($"| {outcome.Id} | {outcome.Name} | {(outcome.Passed ? "PASS" : "FAIL")} | {string.Join(", ", outcome.Families)} | {Sanitize(outcome.Detail)} |");
        }

        builder.AppendLine();
        builder.AppendLine("## Totals");
        builder.AppendLine();
        builder.AppendLine($"- tasks passed: {passed}/{outcomes.Count}");
        builder.AppendLine($"- spec validate pass: {specPass}/{specApplicable.Count}");
        builder.AppendLine($"- XAML emit pass: {xamlPass}/{xamlApplicable.Count}");
        builder.AppendLine($"- unexpected escape-hatch successes: {escapeSuccesses}/{escapeAttempts.Count} (target 0)");
        builder.AppendLine($"- structural families covered: {string.Join(", ", families)}");

        // Always written next to the test assembly; also written into the repo at
        // tests/**/Evals/scorecards/ when the repo root can be located from the
        // test binary, so the recorded result can be committed.
        var text = builder.ToString();
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "scorecard.md"), text);

        if (FindRepoRoot() is { } repoRoot) {
            var committed = Path.Combine(repoRoot, "tests", "UiPath.Engineering.Mcp.Tools.Tests", "Evals", "scorecards", "scorecard.md");
            Directory.CreateDirectory(Path.GetDirectoryName(committed)!);
            File.WriteAllText(committed, text);
        }
    }

    // Walks up from the test binary to the directory that holds .git.
    private static string? FindRepoRoot() {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null) {
            if (Directory.Exists(Path.Combine(directory.FullName, ".git"))) {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }

    private static string Sanitize(string detail) =>
        detail.Replace("\r", " ").Replace("\n", " ").Replace("|", "\\|");

    private static void AssertPassed(EvalOutcome outcome) =>
        Assert.True(outcome.Passed, $"{outcome.Id} {outcome.Name}: {outcome.Detail}");
}