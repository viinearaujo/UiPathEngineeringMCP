using System.Collections.Concurrent;
using UiPath.Engineering.Mcp.Core.Abstractions;
using UiPath.Engineering.Mcp.Core.Models;

namespace UiPath.Engineering.Mcp.Core.Parsing;

/// <summary>
/// Decorates an <see cref="IProjectModelBuilder"/> with a cross-request cache keyed by the
/// normalized project path. Each call recomputes a cheap fingerprint of the project files
/// (count of project.json + *.xaml files plus their newest write time) and only delegates to
/// the inner builder when the fingerprint changed.
/// </summary>
public sealed class CachingProjectModelBuilder : IProjectModelBuilder
{
    private sealed record CacheEntry(UiPathProjectModel Model, long FileCount, long MaxWriteTicks);

    private readonly IProjectModelBuilder _inner;
    private readonly IFilesystemProvider _filesystem;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.OrdinalIgnoreCase);

    public CachingProjectModelBuilder(IProjectModelBuilder inner, IFilesystemProvider filesystem)
    {
        _inner = inner;
        _filesystem = filesystem;
    }

    public async Task<UiPathProjectModel> BuildAsync(string projectPath, CancellationToken cancellationToken = default)
    {
        var key = Path.GetFullPath(projectPath);
        var gate = _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));

        await gate.WaitAsync(cancellationToken);
        try
        {
            if (TryComputeFingerprint(projectPath, out var fileCount, out var maxWriteTicks))
            {
                if (_cache.TryGetValue(key, out var entry) &&
                    entry.FileCount == fileCount && entry.MaxWriteTicks == maxWriteTicks)
                {
                    return entry.Model;
                }

                var built = await _inner.BuildAsync(projectPath, cancellationToken);
                _cache[key] = new CacheEntry(built, fileCount, maxWriteTicks);
                return built;
            }

            // Filesystem inaccessible during fingerprinting: serve the stale cache if we have
            // one, otherwise build directly without caching (we cannot trust a fingerprint).
            if (_cache.TryGetValue(key, out var stale))
            {
                return stale.Model;
            }

            return await _inner.BuildAsync(projectPath, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    private bool TryComputeFingerprint(string projectPath, out long fileCount, out long maxWriteTicks)
    {
        fileCount = 0;
        maxWriteTicks = 0;
        try
        {
            var files = _filesystem.FindXamlFiles(projectPath).ToList();
            var projectJson = _filesystem.FindProjectJson(projectPath);
            if (projectJson is not null)
            {
                files.Add(projectJson);
            }

            foreach (var file in files)
            {
                var ticks = _filesystem.GetLastWriteTimeUtc(file).Ticks;
                if (ticks > maxWriteTicks)
                {
                    maxWriteTicks = ticks;
                }
            }

            fileCount = files.Count;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
    }
}
