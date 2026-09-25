using System.Collections.Concurrent;

namespace UiPath.Engineering.Mcp.Core.Jobs;

/// <summary>
/// In-memory background job store for long CLI work. Thread-safe; finished jobs expire by TTL
/// and the oldest finished entries are evicted when the cap is exceeded.
/// </summary>
public sealed class BackgroundJobStore : IBackgroundJobStore {
    public const int DefaultMaxJobs = 32;
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(30);

    private readonly int _maxJobs;
    private readonly TimeSpan _ttl;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, BackgroundJob> _jobs = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public BackgroundJobStore(
        int maxJobs = DefaultMaxJobs,
        TimeSpan? ttl = null,
        TimeProvider? timeProvider = null) {
        if (maxJobs < 1) {
            throw new ArgumentOutOfRangeException(nameof(maxJobs), maxJobs, "maxJobs must be at least 1.");
        }

        _maxJobs = maxJobs;
        _ttl = ttl ?? DefaultTtl;
        _time = timeProvider ?? TimeProvider.System;
    }

    public BackgroundJob Create(string toolName) {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        PurgeExpired();

        var job = new BackgroundJob {
            JobId = Guid.NewGuid().ToString("N"),
            ToolName = toolName,
            State = BackgroundJobStates.Queued,
            CreatedUtc = _time.GetUtcNow()
        };

        lock (_gate) {
            EvictOldestFinishedUnlocked();
            _jobs[job.JobId] = job;
        }

        return job;
    }

    public bool TryGet(string jobId, out BackgroundJob job) {
        PurgeExpired();
        if (_jobs.TryGetValue(jobId, out job!)) {
            return true;
        }

        job = null!;
        return false;
    }

    public void SetPhase(string jobId, string phase) {
        if (_jobs.TryGetValue(jobId, out var job)) {
            job.Phase = phase;
        }
    }

    public void MarkRunning(string jobId) {
        if (_jobs.TryGetValue(jobId, out var job)) {
            job.State = BackgroundJobStates.Running;
        }
    }

    public void Complete(string jobId, object result) {
        if (!_jobs.TryGetValue(jobId, out var job)) {
            return;
        }

        job.Result = result;
        job.Error = null;
        job.State = BackgroundJobStates.Succeeded;
        job.FinishedUtc = _time.GetUtcNow();
        PurgeExpired();
        lock (_gate) {
            EvictOldestFinishedUnlocked();
        }
    }

    public void Fail(string jobId, string error) {
        if (!_jobs.TryGetValue(jobId, out var job)) {
            return;
        }

        job.Error = error;
        job.Result = null;
        job.State = BackgroundJobStates.Failed;
        job.FinishedUtc = _time.GetUtcNow();
        PurgeExpired();
        lock (_gate) {
            EvictOldestFinishedUnlocked();
        }
    }

    private void PurgeExpired() {
        var now = _time.GetUtcNow();
        foreach (var (id, job) in _jobs) {
            if (job.FinishedUtc is { } finished && now - finished > _ttl) {
                _jobs.TryRemove(id, out _);
            }
        }
    }

    private void EvictOldestFinishedUnlocked() {
        while (_jobs.Count >= _maxJobs) {
            var oldest = _jobs.Values
                .Where(j => j.FinishedUtc is not null)
                .OrderBy(j => j.FinishedUtc)
                .FirstOrDefault();
            if (oldest is null) {
                break;
            }

            _jobs.TryRemove(oldest.JobId, out _);
        }
    }
}
