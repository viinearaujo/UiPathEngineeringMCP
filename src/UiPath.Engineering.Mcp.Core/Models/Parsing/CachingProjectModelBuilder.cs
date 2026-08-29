using System.Collections.Concurrent;
using UiPath.Engineering.Mcp.Core.Abstractions;
using UiPath.Engineering.Mcp.Core.Models;

namespace UiPath.Engineering.Mcp.Core.Parsing;

/// <summary>
/// Decorates an <see cref="IProjectModelBuilder"/> with a cross-request cache keyed by the
/// normalized project path. Each call recomputes a cheap fingerprint of the project files
/// (SHA-256 of sorted path+write-time pairs for project.json + *.xaml + *.cs) and only
/// delegates to the inner builder when the fingerprint changed.
/// </summary>
public sealed class CachingProjectModelBuilder : IProjectModelBuilder {
    private sealed record CacheEntry(UiPathProjectModel Model, string Fingerprint);

    private readonly IProjectModelBuilder _inner;
    private readonly IFilesystemProvider _filesystem;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.OrdinalIgnoreCase);

    public CachingProjectModelBuilder(IProjectModelBuilder inner, IFilesystemProvider filesystem) {
        _inner = inner;
        _filesystem = filesystem;
    }

    public async Task<UiPathProjectModel> BuildAsync(string projectPath, CancellationToken cancellationToken = default) {
        var key = Path.GetFullPath(projectPath);
        var gate = _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));

        await gate.WaitAsync(cancellationToken);
        try {
            if (TryComputeFingerprint(projectPath, out var fingerprint)) {
                if (_cache.TryGetValue(key, out var entry) && entry.Fingerprint == fingerprint) {
                    return entry.Model;
                }

                var built = await _inner.BuildAsync(projectPath, cancellationToken);
                _cache[key] = new CacheEntry(built, fingerprint);
                return built;
            }

            // Filesystem inaccessible during fingerprinting: serve the stale cache if we have
            // one, otherwise build directly without caching (we cannot trust a fingerprint).
            if (_cache.TryGetValue(key, out var stale)) {
                return stale.Model;
            }

            return await _inner.BuildAsync(projectPath, cancellationToken);
        } finally {
            gate.Release();
        }
    }

    private bool TryComputeFingerprint(string projectPath, out string fingerprint) {
        fingerprint = string.Empty;
        try {
            var files = _filesystem.FindXamlFiles(projectPath)
                .Concat(_filesystem.FindCSharpFiles(projectPath))
                .ToList();
            var projectJson = _filesystem.FindProjectJson(projectPath);
            if (projectJson is not null) {
                files.Add(projectJson);
            }

            var sb = new System.Text.StringBuilder();
            foreach (var file in files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase)) {
                var ticks = _filesystem.GetLastWriteTimeUtc(file).Ticks;
                sb.Append(file).Append('\0').Append(ticks).Append('\n');
            }

            fingerprint = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(sb.ToString())));
            return true;
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FileNotFoundException or DirectoryNotFoundException) {
            return false;
        }
    }
}
