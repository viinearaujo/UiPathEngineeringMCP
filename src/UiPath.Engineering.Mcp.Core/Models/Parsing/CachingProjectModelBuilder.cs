using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using UiPath.Engineering.Mcp.Core.Abstractions;
using UiPath.Engineering.Mcp.Core.Caching;
using UiPath.Engineering.Mcp.Core.Models;

namespace UiPath.Engineering.Mcp.Core.Parsing;

/// <summary>
/// Decorates an <see cref="IProjectModelBuilder"/> with a bounded cross-request cache
/// keyed by the normalized project path. Each call recomputes a SHA-256 fingerprint of
/// the project files (sorted path+write-time pairs for project.json + *.xaml + *.cs)
/// and only delegates to the inner builder when the fingerprint changed. Fingerprint
/// failure serves a cached model with <see cref="UiPathProjectModel.Stale"/> set.
/// </summary>
public sealed class CachingProjectModelBuilder : IProjectModelBuilder, IDisposable {
    private sealed record CacheEntry(UiPathProjectModel Model, string Fingerprint);

    private readonly IProjectModelBuilder _inner;
    private readonly IFilesystemProvider _filesystem;
    private readonly BoundedCache<CacheEntry> _cache;
    private readonly ILogger<CachingProjectModelBuilder> _logger;

    public CachingProjectModelBuilder(
        IProjectModelBuilder inner,
        IFilesystemProvider filesystem,
        ILogger<CachingProjectModelBuilder>? logger = null)
        : this(inner, filesystem, BoundedCache<CacheEntry>.DefaultMaxEntries, null, null, logger) {
    }

    public CachingProjectModelBuilder(
        IProjectModelBuilder inner,
        IFilesystemProvider filesystem,
        int maxEntries,
        TimeSpan? ttl = null,
        TimeProvider? timeProvider = null,
        ILogger<CachingProjectModelBuilder>? logger = null) {
        _inner = inner;
        _filesystem = filesystem;
        _cache = new BoundedCache<CacheEntry>(maxEntries, ttl, timeProvider);
        _logger = logger ?? NullLogger<CachingProjectModelBuilder>.Instance;
    }

    internal int CacheEntryCount => _cache.EntryCount;

    internal int CacheLockCount => _cache.LockCount;

    public async Task<UiPathProjectModel> BuildAsync(string projectPath, CancellationToken cancellationToken = default) {
        var key = Path.GetFullPath(projectPath);
        return await _cache.RunExclusiveAsync(key, async ct => {
            if (ProjectFingerprint.TryComputeProjectFiles(_filesystem, projectPath, out var fingerprint)) {
                if (_cache.TryGet(key, out var entry) && entry.Fingerprint == fingerprint) {
                    _logger.LogDebug("Project model cache hit for {CacheKey}", key);
                    entry.Model.Stale = false;
                    return entry.Model;
                }

                _logger.LogDebug("Project model cache miss for {CacheKey}", key);
                var built = await _inner.BuildAsync(projectPath, ct);
                built.Stale = false;
                _cache.Set(key, new CacheEntry(built, fingerprint));
                return built;
            }

            if (_cache.TryGet(key, out var stale, includeExpired: true)) {
                _logger.LogInformation("Project model cache stale for {CacheKey}", key);
                stale.Model.Stale = true;
                return stale.Model;
            }

            _logger.LogDebug("Project model cache miss for {CacheKey}", key);
            return await _inner.BuildAsync(projectPath, ct);
        }, cancellationToken);
    }

    public void Dispose() => _cache.Dispose();
}
