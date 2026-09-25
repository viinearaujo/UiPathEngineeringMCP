using UiPath.Engineering.Mcp.Core.Jobs;

namespace UiPath.Engineering.Mcp.Core.Tests;

public class BackgroundJobStoreTests {
    [Fact]
    public void Complete_StoresResultAndSucceededState() {
        var store = new BackgroundJobStore();
        var job = store.Create("validate_project");
        store.MarkRunning(job.JobId);
        store.SetPhase(job.JobId, "validating");
        store.Complete(job.JobId, new { ok = true });

        Assert.True(store.TryGet(job.JobId, out var loaded));
        Assert.Equal(BackgroundJobStates.Succeeded, loaded.State);
        Assert.Equal("validating", loaded.Phase);
        Assert.NotNull(loaded.Result);
        Assert.NotNull(loaded.FinishedUtc);
    }

    [Fact]
    public void Fail_StoresError() {
        var store = new BackgroundJobStore();
        var job = store.Create("compile_project");
        store.Fail(job.JobId, "boom");

        Assert.True(store.TryGet(job.JobId, out var loaded));
        Assert.Equal(BackgroundJobStates.Failed, loaded.State);
        Assert.Equal("boom", loaded.Error);
        Assert.Null(loaded.Result);
    }

    [Fact]
    public void EvictsOldestFinished_WhenCapExceeded() {
        var store = new BackgroundJobStore(maxJobs: 2);
        var first = store.Create("a");
        store.Complete(first.JobId, "done-a");
        var second = store.Create("b");
        store.Complete(second.JobId, "done-b");
        var third = store.Create("c");

        Assert.False(store.TryGet(first.JobId, out _));
        Assert.True(store.TryGet(second.JobId, out _));
        Assert.True(store.TryGet(third.JobId, out _));
    }

    [Fact]
    public void PurgesFinishedJobs_PastTtl() {
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var store = new BackgroundJobStore(maxJobs: 8, ttl: TimeSpan.FromMinutes(1), timeProvider: time);
        var job = store.Create("validate_project");
        store.Complete(job.JobId, "done");

        time.Advance(TimeSpan.FromMinutes(2));
        Assert.False(store.TryGet(job.JobId, out _));
    }

    private sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now += delta;
    }
}
