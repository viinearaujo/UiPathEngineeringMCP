using System.Collections.Concurrent;
using System.Text.Json;
using UiPath.Engineering.Mcp.Core.Abstractions;
using UiPath.Engineering.Mcp.Core.Models;
using UiPath.Engineering.Mcp.Core.Parsing;

namespace UiPath.Engineering.Mcp.Core.CodeAnalysis;

/// <summary>
/// Decorates an <see cref="ICSharpContextBuilder"/> with a cross-request cache keyed by
/// the normalized project path. Each call recomputes a cheap fingerprint (count of
/// *.cs files plus project.json, and their newest write time, plus the write times of
/// the NuGet package folders backing the project's dependencies) and only rebuilds the
/// Roslyn compilation when the fingerprint changed. The NuGet folders live outside the
/// project tree, but a `dotnet restore` changes only them — without them in the
/// fingerprint a stale partial/syntax-only compilation would be served forever.
/// Mirrors CachingProjectModelBuilder.
/// </summary>
public sealed class CSharpAnalysisCache : ICSharpContextBuilder {
    private sealed record CacheEntry(CSharpAnalysisContext Context, long FileCount, long MaxWriteTicks);

    private readonly ICSharpContextBuilder _inner;
    private readonly IFilesystemProvider _filesystem;
    private readonly NuGetReferenceResolver _resolver;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.OrdinalIgnoreCase);

    public CSharpAnalysisCache(ICSharpContextBuilder inner, IFilesystemProvider filesystem, NuGetReferenceResolver resolver) {
        _inner = inner;
        _filesystem = filesystem;
        _resolver = resolver;
    }

    public async Task<CSharpAnalysisContext> BuildAsync(string projectPath, CancellationToken cancellationToken = default) {
        var key = Path.GetFullPath(projectPath);
        var gate = _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));

        await gate.WaitAsync(cancellationToken);
        try {
            if (TryComputeFingerprint(projectPath, out var fileCount, out var maxWriteTicks)) {
                if (_cache.TryGetValue(key, out var entry) &&
                    entry.FileCount == fileCount && entry.MaxWriteTicks == maxWriteTicks) {
                    return entry.Context;
                }

                var built = await _inner.BuildAsync(projectPath, cancellationToken);
                _cache[key] = new CacheEntry(built, fileCount, maxWriteTicks);
                return built;
            }

            // Filesystem inaccessible during fingerprinting: serve stale cache if present,
            // otherwise build directly without caching (we cannot trust a fingerprint).
            if (_cache.TryGetValue(key, out var stale)) {
                return stale.Context;
            }

            return await _inner.BuildAsync(projectPath, cancellationToken);
        } finally {
            gate.Release();
        }
    }

    private bool TryComputeFingerprint(string projectPath, out long fileCount, out long maxWriteTicks) {
        fileCount = 0;
        maxWriteTicks = 0;
        try {
            var files = _filesystem.FindCSharpFiles(projectPath).ToList();
            var projectJson = _filesystem.FindProjectJson(projectPath);
            if (projectJson is not null) {
                files.Add(projectJson);
            }

            foreach (var file in files) {
                var ticks = _filesystem.GetLastWriteTimeUtc(file).Ticks;
                if (ticks > maxWriteTicks) {
                    maxWriteTicks = ticks;
                }
            }

            foreach (var folder in GetPackageFolders(projectJson)) {
                var ticks = SafeGetWriteTicks(folder);
                if (ticks > maxWriteTicks) {
                    maxWriteTicks = ticks;
                }
            }

            fileCount = files.Count;
            return true;
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FileNotFoundException or DirectoryNotFoundException) {
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
