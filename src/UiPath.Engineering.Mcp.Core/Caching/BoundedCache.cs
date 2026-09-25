using System.Collections.Concurrent;

namespace UiPath.Engineering.Mcp.Core.Caching;

/// <summary>
/// Bounded in-process cache: max entries (LRU), sliding TTL, and a per-key
/// <see cref="SemaphoreSlim"/>. Keys are project paths, so the key space is small; the
/// per-key semaphores are kept for the lifetime of the cache and disposed only in
/// <see cref="Dispose"/>. Disposing an idle semaphore on eviction can race another
/// caller between its <c>GetOrAdd</c> and <c>WaitAsync</c>, so it is deliberately avoided.
/// </summary>
public sealed class BoundedCache<TValue> : IDisposable {
    public const int DefaultMaxEntries = 32;
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(15);

    private readonly int _maxEntries;
    private readonly TimeSpan _ttl;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, Entry> _entries;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks;
    private readonly Action<string, TValue>? _onEvicted;
    private long _accessClock;
    private bool _disposed;

    public BoundedCache(
        int maxEntries = DefaultMaxEntries,
        TimeSpan? ttl = null,
        TimeProvider? timeProvider = null,
        Action<string, TValue>? onEvicted = null) {
        if (maxEntries < 1) {
            throw new ArgumentOutOfRangeException(nameof(maxEntries), maxEntries, "maxEntries must be at least 1.");
        }

        _maxEntries = maxEntries;
        _ttl = ttl ?? DefaultTtl;
        _time = timeProvider ?? TimeProvider.System;
        _entries = new ConcurrentDictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        _locks = new ConcurrentDictionary<string, SemaphoreSlim>(StringComparer.OrdinalIgnoreCase);
        _onEvicted = onEvicted;
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
                if (_entries.TryRemove(key, out var removed)) {
                    _onEvicted?.Invoke(key, removed.Value);
                }

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
        if (_entries.TryGetValue(key, out var previous)) {
            _entries[key] = new Entry(value, now, access);
            // Replacing the same key is not an eviction of a different project.
            _ = previous;
        } else {
            _entries[key] = new Entry(value, now, access);
        }

        EvictOverflow(key);
    }

    public void Dispose() {
        if (_disposed) {
            return;
        }

        _disposed = true;
        foreach (var pair in _entries) {
            if (_entries.TryRemove(pair.Key, out var entry)) {
                _onEvicted?.Invoke(pair.Key, entry.Value);
            }
        }

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

            if (_entries.TryRemove(victim, out var removed)) {
                _onEvicted?.Invoke(victim, removed.Value);
            }
        }
    }

    private bool IsExpired(Entry entry, DateTimeOffset now) =>
        _ttl > TimeSpan.Zero && now - entry.LastAccessUtc > _ttl;

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
