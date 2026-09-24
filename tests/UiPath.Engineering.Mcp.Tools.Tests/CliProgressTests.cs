using ModelContextProtocol;

namespace UiPath.Engineering.Mcp.Tools.Tests;

/// <summary>
/// Unit coverage for the shared progress helper the CLI-backed tools report through. The helper
/// runs on every CLI call, so a null sink (a client that sent no progress token) must be a no-op
/// and the reported step must never exceed the declared total.
/// </summary>
public class CliProgressTests {
    private sealed class RecordingProgress : IProgress<ProgressNotificationValue> {
        public List<ProgressNotificationValue> Values { get; } = [];
        public void Report(ProgressNotificationValue value) => Values.Add(value);
    }

    [Fact]
    public void NullSink_IsANoOp() {
        var scope = CliToolSupport.ProgressFor(null, "start", total: 2);

        scope.Step("done");
        scope.Step("again");
    }

    [Fact]
    public void Start_ReportsStepZeroWithTheDeclaredTotal() {
        var progress = new RecordingProgress();

        CliToolSupport.ProgressFor(progress, "starting", total: 3);

        var only = Assert.Single(progress.Values);
        Assert.Equal(0, only.Progress);
        Assert.Equal(3, only.Total);
        Assert.Equal("starting", only.Message);
    }

    [Fact]
    public void Steps_AdvanceMonotonically() {
        var progress = new RecordingProgress();
        var scope = CliToolSupport.ProgressFor(progress, "start", total: 3);

        scope.Step("one");
        scope.Step("two");
        scope.Step("three");

        Assert.Equal([0, 1, 2, 3], progress.Values.Select(v => v.Progress));
    }

    [Fact]
    public void Step_IsClampedToTheTotal() {
        var progress = new RecordingProgress();
        var scope = CliToolSupport.ProgressFor(progress, "start", total: 2);

        scope.Step("first");
        scope.Step("second");
        scope.Step("past the total");

        Assert.Equal(2, progress.Values[^1].Progress);
        Assert.All(progress.Values, v => Assert.Equal(2, v.Total));
    }

    [Fact]
    public void NonPositiveTotal_IsNormalizedToOne() {
        var progress = new RecordingProgress();

        CliToolSupport.ProgressFor(progress, "start", total: 0);

        Assert.Equal(1, Assert.Single(progress.Values).Total);
    }
}