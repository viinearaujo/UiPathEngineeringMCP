using System.Collections.Concurrent;

namespace UiPath.Engineering.Mcp.Core.Caching;

/// <summary>
/// Bounded in-process cache: max entries (LRU), sliding TTL, and a per-key
/// <see cref="SemaphoreSlim"/> that is disposed once the key is no longer cached
/// and no waiter holds it.
/// </summary>
public sealed class BoundedCache<TValue> : IDisposable {
    public const int DefaultMaxEntries = 32;
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(15);

    private readonly int _maxEntries;
    private readonly TimeSpan _ttl;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, Entry> _entries;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks;
    private long _accessClock;
    private bool _disposed;

    public BoundedCache(
        int maxEntries = DefaultMaxEntries,
        TimeSpan? ttl = null,
        TimeProvider? timeProvider = null) {
        if (maxEntries < 1) {
            throw new ArgumentOutOfRangeException(nameof(maxEntries), maxEntries, "maxEntries must be at least 1.");
        }

        _maxEntries = maxEntries;
        _ttl = ttl ?? DefaultTtl;
        _time = timeProvider ?? TimeProvider.System;
        _entries = new ConcurrentDictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        _locks = new ConcurrentDictionary<string, SemaphoreSlim>(StringComparer.OrdinalIgnoreCase);
    }

    internal int EntryCount => _entries.Count;

    internal int LockCount => _locks.Count;

    public async Task<TResult> RunExclusiveAsync<TResult>(
        string key,
        Func<CancellationToken, Task<TResult>> action,
        CancellationToken cancellationToken = default) {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var gate = _locks.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try {
            return await action(cancellationToken);
        } finally {
            gate.Release();
            TryDisposeUnusedLock(key, gate);
        }
    }

    public bool TryGet(string key, out TValue value, bool includeExpired = false) {
        value = default!;
        if (!_entries.TryGetValue(key, out var entry)) {
            return false;
        }

        var now = _time.GetUtcNow();
        if (IsExpired(entry, now)) {
            if (!includeExpired) {
                _entries.TryRemove(key, out _);
                return false;
            }

            value = entry.Value;
            return true;
        }

        entry.LastAccessUtc = now;
        entry.AccessOrder = Interlocked.Increment(ref _accessClock);
        value = entry.Value;
        return true;
    }

    public void Set(string key, TValue value) {
        var now = _time.GetUtcNow();
        var access = Interlocked.Increment(ref _accessClock);
        _entries[key] = new Entry(value, now, access);
        EvictOverflow(key);
    }

    public void Dispose() {
        if (_disposed) {
            return;
        }

        _disposed = true;
        _entries.Clear();
        foreach (var key in _locks.Keys) {
            if (_locks.TryRemove(key, out var gate)) {
                gate.Dispose();
            }
        }
    }

    private void EvictOverflow(string keepKey) {
        while (_entries.Count > _maxEntries) {
            string? victim = null;
            var minAccess = long.MaxValue;
            foreach (var pair in _entries) {
                if (string.Equals(pair.Key, keepKey, StringComparison.OrdinalIgnoreCase)) {
                    continue;
                }

                if (pair.Value.AccessOrder <= minAccess) {
                    minAccess = pair.Value.AccessOrder;
                    victim = pair.Key;
                }
            }

            if (victim is null) {
                break;
            }

            if (_entries.TryRemove(victim, out _)) {
                TryDisposeLockIfIdle(victim);
            }
        }
    }

    private bool IsExpired(Entry entry, DateTimeOffset now) =>
        _ttl > TimeSpan.Zero && now - entry.LastAccessUtc > _ttl;

    private void TryDisposeLockIfIdle(string key) {
        if (!_locks.TryGetValue(key, out var gate) || gate.CurrentCount != 1) {
            return;
        }

        if (_locks.TryRemove(key, out var removed) && ReferenceEquals(removed, gate) && gate.CurrentCount == 1) {
            gate.Dispose();
        } else if (removed is not null && !ReferenceEquals(removed, gate)) {
            _locks.TryAdd(key, removed);
        }
    }

    private void TryDisposeUnusedLock(string key, SemaphoreSlim gate) {
        if (_entries.ContainsKey(key)) {
            return;
        }

        _locks.TryRemove(key, out var removed);
        if (removed is not null && !ReferenceEquals(removed, gate)) {
            _locks.TryAdd(key, removed);
            return;
        }

        if (gate.CurrentCount == 1) {
            gate.Dispose();
        } else {
            _locks.TryAdd(key, gate);
        }
    }

    private sealed class Entry {
        public Entry(TValue value, DateTimeOffset lastAccessUtc, long accessOrder) {
            Value = value;
            LastAccessUtc = lastAccessUtc;
            AccessOrder = accessOrder;
        }

        public TValue Value { get; }
        public DateTimeOffset LastAccessUtc { get; set; }
        public long AccessOrder { get; set; }
    }
}
