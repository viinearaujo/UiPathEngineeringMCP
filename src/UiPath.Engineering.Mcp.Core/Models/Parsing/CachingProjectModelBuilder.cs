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
    private readonly IProjectModelBuilder _inner;
    private readonly IFilesystemProvider _filesystem;
    private readonly FingerprintedCache<UiPathProjectModel> _cache;

    public CachingProjectModelBuilder(
        IProjectModelBuilder inner,
        IFilesystemProvider filesystem,
        ILogger<CachingProjectModelBuilder>? logger = null)
        : this(inner, filesystem, BoundedCache<UiPathProjectModel>.DefaultMaxEntries, null, null, logger) {
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
        _cache = new FingerprintedCache<UiPathProjectModel>(
            "Project model",
            maxEntries,
            ttl,
            timeProvider,
            logger ?? NullLogger<CachingProjectModelBuilder>.Instance);
    }

    internal int CacheEntryCount => _cache.EntryCount;

    internal int CacheLockCount => _cache.LockCount;

    public Task<UiPathProjectModel> BuildAsync(string projectPath, CancellationToken cancellationToken = default) =>
        _cache.GetOrBuildAsync(
            projectPath,
            path => ProjectFingerprint.TryComputeProjectFiles(_filesystem, path, out var fingerprint) ? fingerprint : null,
            ct => _inner.BuildAsync(projectPath, ct),
            (model, stale) => model.Stale = stale,
            cancellationToken);

    public void Dispose() => _cache.Dispose();
}
