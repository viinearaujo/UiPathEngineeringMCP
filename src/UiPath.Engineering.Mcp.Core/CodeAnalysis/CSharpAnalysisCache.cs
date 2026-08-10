using System.Collections.Concurrent;
using UiPath.Engineering.Mcp.Core.Abstractions;

namespace UiPath.Engineering.Mcp.Core.CodeAnalysis;

/// <summary>
/// Decorates an <see cref="ICSharpContextBuilder"/> with a cross-request cache keyed by
/// the normalized project path. Each call recomputes a cheap fingerprint (count of
/// *.cs files plus project.json, and their newest write time) and only rebuilds the
/// Roslyn compilation when the fingerprint changed. Mirrors CachingProjectModelBuilder.
/// </summary>
public sealed class CSharpAnalysisCache : ICSharpContextBuilder {
    private sealed record CacheEntry(CSharpAnalysisContext Context, long FileCount, long MaxWriteTicks);

    private readonly ICSharpContextBuilder _inner;
    private readonly IFilesystemProvider _filesystem;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.OrdinalIgnoreCase);

    public CSharpAnalysisCache(ICSharpContextBuilder inner, IFilesystemProvider filesystem) {
        _inner = inner;
        _filesystem = filesystem;
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

            fileCount = files.Count;
            return true;
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FileNotFoundException or DirectoryNotFoundException) {
            return false;
        }
    }
}
