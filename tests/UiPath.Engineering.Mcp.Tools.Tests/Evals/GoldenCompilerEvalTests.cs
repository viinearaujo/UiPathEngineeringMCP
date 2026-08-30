using Xunit.Abstractions;

namespace UiPath.Engineering.Mcp.Tools.Tests.Evals;

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
    public async Task Scorecard_ReportsValidatePassAndEscapeHatchRate() {
        var outcomes = await GoldenEvalTasks.RunAll();
        foreach (var outcome in outcomes) {
            _output.WriteLine($"{(outcome.Passed ? "PASS" : "FAIL")}  {outcome.Id,-28} {outcome.Name,-40} {outcome.Detail}");
        }

        var specApplicable = outcomes.Where(o => o.SpecValidates.HasValue).ToList();
        var specPass = specApplicable.Count(o => o.SpecValidates == true);
        var xamlApplicable = outcomes.Where(o => o.XamlEmits.HasValue).ToList();
        var xamlPass = xamlApplicable.Count(o => o.XamlEmits == true);
        var escapeAttempts = outcomes.Where(o => o.UnknownXamlWriteSucceeded.HasValue).ToList();
        var escapeSuccesses = escapeAttempts.Count(o => o.UnknownXamlWriteSucceeded == true);
        var passed = outcomes.Count(o => o.Passed);

        _output.WriteLine("");
        _output.WriteLine($"tasks passed:            {passed}/{outcomes.Count}");
        _output.WriteLine($"spec validate pass:      {specPass}/{specApplicable.Count}");
        _output.WriteLine($"XAML emit pass:          {xamlPass}/{xamlApplicable.Count}");
        _output.WriteLine($"XAML escape-hatch rate:  {escapeSuccesses}/{escapeAttempts.Count} (target 0)");

        Assert.Equal(10, outcomes.Count);
        Assert.Equal(10, passed);
        Assert.Equal(specApplicable.Count, specPass);
        Assert.Equal(xamlApplicable.Count, xamlPass);
        Assert.Equal(0, escapeSuccesses);
    }

    private static void AssertPassed(EvalOutcome outcome) =>
        Assert.True(outcome.Passed, $"{outcome.Id} {outcome.Name}: {outcome.Detail}");
}
