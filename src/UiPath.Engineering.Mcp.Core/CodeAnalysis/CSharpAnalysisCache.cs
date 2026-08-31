using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using UiPath.Engineering.Mcp.Core.Abstractions;
using UiPath.Engineering.Mcp.Core.Caching;
using UiPath.Engineering.Mcp.Core.Models;
using UiPath.Engineering.Mcp.Core.Parsing;

namespace UiPath.Engineering.Mcp.Core.CodeAnalysis;

/// <summary>
/// Decorates an <see cref="ICSharpContextBuilder"/> with a bounded cross-request cache
/// keyed by the normalized project path. Each call recomputes the project-model SHA
/// fingerprint over *.cs + project.json, plus the write times of the NuGet package
/// folders backing the project's dependencies, and only rebuilds the Roslyn compilation
/// when the fingerprint changed. The NuGet folders live outside the project tree, but a
/// `dotnet restore` changes only them — without them in the fingerprint a stale
/// partial/syntax-only compilation would be served forever.
/// Fingerprint failure serves a cached context with <see cref="CSharpAnalysisContext.Stale"/> set.
/// </summary>
public sealed class CSharpAnalysisCache : ICSharpContextBuilder, IDisposable {
    private sealed record CacheEntry(CSharpAnalysisContext Context, string Fingerprint);

    private readonly ICSharpContextBuilder _inner;
    private readonly IFilesystemProvider _filesystem;
    private readonly NuGetReferenceResolver _resolver;
    private readonly BoundedCache<CacheEntry> _cache;
    private readonly ILogger<CSharpAnalysisCache> _logger;

    public CSharpAnalysisCache(
        ICSharpContextBuilder inner,
        IFilesystemProvider filesystem,
        NuGetReferenceResolver resolver,
        ILogger<CSharpAnalysisCache>? logger = null)
        : this(inner, filesystem, resolver, BoundedCache<CacheEntry>.DefaultMaxEntries, null, null, logger) {
    }

    public CSharpAnalysisCache(
        ICSharpContextBuilder inner,
        IFilesystemProvider filesystem,
        NuGetReferenceResolver resolver,
        int maxEntries,
        TimeSpan? ttl = null,
        TimeProvider? timeProvider = null,
        ILogger<CSharpAnalysisCache>? logger = null) {
        _inner = inner;
        _filesystem = filesystem;
        _resolver = resolver;
        _cache = new BoundedCache<CacheEntry>(maxEntries, ttl, timeProvider);
        _logger = logger ?? NullLogger<CSharpAnalysisCache>.Instance;
    }

    internal int CacheEntryCount => _cache.EntryCount;

    internal int CacheLockCount => _cache.LockCount;

    public async Task<CSharpAnalysisContext> BuildAsync(string projectPath, CancellationToken cancellationToken = default) {
        var key = Path.GetFullPath(projectPath);
        return await _cache.RunExclusiveAsync(key, async ct => {
            if (TryComputeFingerprint(projectPath, out var fingerprint)) {
                if (_cache.TryGet(key, out var entry) && entry.Fingerprint == fingerprint) {
                    _logger.LogDebug("C# analysis cache hit for {CacheKey}", key);
                    entry.Context.Stale = false;
                    return entry.Context;
                }

                _logger.LogDebug("C# analysis cache miss for {CacheKey}", key);
                var built = await _inner.BuildAsync(projectPath, ct);
                built.Stale = false;
                _cache.Set(key, new CacheEntry(built, fingerprint));
                return built;
            }

            if (_cache.TryGet(key, out var stale, includeExpired: true)) {
                _logger.LogInformation("C# analysis cache stale for {CacheKey}", key);
                stale.Context.Stale = true;
                return stale.Context;
            }

            _logger.LogDebug("C# analysis cache miss for {CacheKey}", key);
            return await _inner.BuildAsync(projectPath, ct);
        }, cancellationToken);
    }

    public void Dispose() => _cache.Dispose();

    private bool TryComputeFingerprint(string projectPath, out string fingerprint) {
        fingerprint = string.Empty;
        try {
            var files = _filesystem.FindCSharpFiles(projectPath).ToList();
            var projectJson = _filesystem.FindProjectJson(projectPath);
            if (projectJson is not null) {
                files.Add(projectJson);
            }

            var extra = GetPackageFolders(projectJson)
                .Select(folder => (Path: folder, Ticks: SafeGetWriteTicks(folder)));
            return ProjectFingerprint.TryCompute(_filesystem, files, out fingerprint, extra);
        } catch (Exception ex) when (ProjectFingerprint.IsIoFailure(ex)) {
            return false;
        }
    }

    /// <summary>
    /// Yields the NuGet folders backing the project's declared dependencies: each
    /// package's id folder and its declared-version folder under the global packages
    /// folder. A `dotnet restore` creates or updates these folders, which changes
    /// their write times and thus invalidates the fingerprint.
    /// </summary>
    private IEnumerable<string> GetPackageFolders(string? projectJson) {
        if (projectJson is null) {
            yield break;
        }

        List<PackageModel> packages;
        try {
            packages = new ProjectJsonParser(_filesystem).Parse(projectJson, projectRoot: "").Packages.ToList();
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FileNotFoundException or JsonException) {
            // project.json unreadable or malformed: the inner build will surface the
            // real error; the fingerprint simply gets no NuGet component.
            yield break;
        }

        if (packages.Count == 0) {
            yield break;
        }

        var packagesFolder = _resolver.GetPackagesFolder();
        if (packagesFolder is null) {
            yield break;
        }

        foreach (var package in packages) {
            var idFolder = Path.Combine(packagesFolder, package.Id.ToLowerInvariant());
            yield return idFolder;
            yield return Path.Combine(idFolder, package.Version.ToLowerInvariant());
        }
    }

    // Missing package folders contribute nothing (constant ticks); when a restore
    // creates them, the ticks change and the fingerprint invalidates.
    private long SafeGetWriteTicks(string path) {
        try {
            return _filesystem.GetLastWriteTimeUtc(path).Ticks;
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FileNotFoundException or DirectoryNotFoundException or ArgumentException) {
            return 0;
        }
    }
}
