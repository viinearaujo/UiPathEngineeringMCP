using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace UiPath.Engineering.Mcp.Core.Caching;

/// <summary>
/// Cross-request decorator over <see cref="BoundedCache{TValue}"/> keyed by the normalized
/// project path. Each call recomputes a fingerprint for the project and rebuilds the cached
/// value only when the fingerprint changed. When an <see cref="IProjectChangeWatcher"/> is
/// active and not dirty, the previous fingerprint is reused without walking the tree.
/// A fingerprint failure serves the cached value with the caller's stale flag set rather
/// than throwing; an inner build exception is never cached. Watchers are disposed when
/// the project entry is evicted or the cache is disposed.
/// </summary>
public sealed class FingerprintedCache<TValue> : IDisposable {
    private sealed record CacheEntry(TValue Value, string Fingerprint);

    private readonly string _label;
    private readonly BoundedCache<CacheEntry> _cache;
    private readonly ILogger _logger;
    private readonly IProjectChangeWatcherFactory? _watcherFactory;
    private readonly bool _reuseWhenClean;
    private readonly ConcurrentDictionary<string, IProjectChangeWatcher> _watchers =
        new(StringComparer.OrdinalIgnoreCase);

    public FingerprintedCache(
        string label,
        int maxEntries = BoundedCache<CacheEntry>.DefaultMaxEntries,
        TimeSpan? ttl = null,
        TimeProvider? timeProvider = null,
        ILogger? logger = null,
        IProjectChangeWatcherFactory? watcherFactory = null,
        bool reuseWhenClean = true) {
        _label = label;
        _watcherFactory = watcherFactory;
        _reuseWhenClean = reuseWhenClean;
        _logger = logger ?? NullLogger.Instance;
        _cache = new BoundedCache<CacheEntry>(maxEntries, ttl, timeProvider, OnEntryEvicted);
    }

    internal int EntryCount => _cache.EntryCount;

    internal int LockCount => _cache.LockCount;

    /// <summary>
    /// Returns the cached value for <paramref name="projectPath"/> when its fingerprint is
    /// unchanged, otherwise builds one. <paramref name="tryComputeFingerprint"/> returns null
    /// when the fingerprint cannot be computed; <paramref name="setStale"/> flags a served
    /// value whose freshness could not be verified.
    /// </summary>
    public async Task<TValue> GetOrBuildAsync(
        string projectPath,
        Func<string, string?> tryComputeFingerprint,
        Func<CancellationToken, Task<TValue>> buildAsync,
        Action<TValue, bool> setStale,
        CancellationToken cancellationToken = default) {
        var key = Path.GetFullPath(projectPath);
        return await _cache.RunExclusiveAsync(key, async ct => {
            var watcher = GetOrCreateWatcher(key);
            if (_reuseWhenClean
                && watcher is { IsActive: true, IsDirty: false }
                && _cache.TryGet(key, out var cleanHit)) {
                _logger.LogDebug("{Label} cache hit (watcher clean) for {CacheKey}", _label, key);
                setStale(cleanHit.Value, false);
                return cleanHit.Value;
            }

            if (tryComputeFingerprint(projectPath) is { } fingerprint) {
                if (_cache.TryGet(key, out var entry) && entry.Fingerprint == fingerprint) {
                    _logger.LogDebug("{Label} cache hit for {CacheKey}", _label, key);
                    setStale(entry.Value, false);
                    watcher?.MarkClean();
                    return entry.Value;
                }

                _logger.LogDebug("{Label} cache miss for {CacheKey}", _label, key);
                var built = await buildAsync(ct);
                setStale(built, false);
                _cache.Set(key, new CacheEntry(built, fingerprint));
                watcher?.MarkClean();
                return built;
            }

            if (_cache.TryGet(key, out var stale, includeExpired: true)) {
                _logger.LogInformation("{Label} cache stale for {CacheKey}", _label, key);
                setStale(stale.Value, true);
                return stale.Value;
            }

            _logger.LogDebug("{Label} cache miss for {CacheKey}", _label, key);
            return await buildAsync(ct);
        }, cancellationToken);
    }

    public void Dispose() {
        _cache.Dispose();
        foreach (var key in _watchers.Keys) {
            if (_watchers.TryRemove(key, out var watcher)) {
                watcher.Dispose();
            }
        }
    }

    private IProjectChangeWatcher? GetOrCreateWatcher(string key) {
        if (_watcherFactory is null) {
            return null;
        }

        return _watchers.GetOrAdd(key, static (path, factory) =>
            factory.TryCreate(path) ?? new InactiveWatcher(), _watcherFactory);
    }

    private void OnEntryEvicted(string key, CacheEntry _) {
        if (_watchers.TryRemove(key, out var watcher)) {
            watcher.Dispose();
        }
    }

    /// <summary>Placeholder when the factory cannot start a real watcher.</summary>
    private sealed class InactiveWatcher : IProjectChangeWatcher {
        public bool IsActive => false;
        public bool IsDirty => true;
        public void MarkClean() { }
        public void Dispose() { }
    }
}
