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
/// folders backing the project's dependencies and of the Studio-generated coded-workflow
/// sources under `.local/.codedworkflows/`, and only rebuilds the Roslyn compilation
/// when the fingerprint changed. The NuGet folders live outside the project tree, but a
/// `dotnet restore` changes only them — without them in the fingerprint a stale
/// partial/syntax-only compilation would be served forever. The generated sources live
/// inside the tree but are invisible to <see cref="IFilesystemProvider"/> discovery, so
/// without their write times a regenerated Object Repository would be served from a stale
/// compilation forever.
/// Fingerprint failure serves a cached context with <see cref="CSharpAnalysisContext.Stale"/> set.
/// </summary>
public sealed class CSharpAnalysisCache : ICSharpContextBuilder, IDisposable {
    /// <summary>
    /// Roslyn compilations are large and retained for the whole sliding TTL, so the
    /// cache is deliberately smaller than the general default.
    /// </summary>
    public const int DefaultMaxEntries = 8;

    private readonly ICSharpContextBuilder _inner;
    private readonly IFilesystemProvider _filesystem;
    private readonly NuGetReferenceResolver _resolver;
    private readonly FingerprintedCache<CSharpAnalysisContext> _cache;

    public CSharpAnalysisCache(
        ICSharpContextBuilder inner,
        IFilesystemProvider filesystem,
        NuGetReferenceResolver resolver,
        ILogger<CSharpAnalysisCache>? logger = null)
        : this(inner, filesystem, resolver, DefaultMaxEntries, null, null, logger) {
    }

    public CSharpAnalysisCache(
        ICSharpContextBuilder inner,
        IFilesystemProvider filesystem,
        NuGetReferenceResolver resolver,
        int maxEntries,
        TimeSpan? ttl = null,
        TimeProvider? timeProvider = null,
        ILogger<CSharpAnalysisCache>? logger = null,
        IProjectChangeWatcherFactory? watcherFactory = null) {
        _inner = inner;
        _filesystem = filesystem;
        _resolver = resolver;
        // Reuse-when-clean is off: the fingerprint also covers NuGet folders and
        // `.local/.codedworkflows` sources the project-file watcher does not see.
        _cache = new FingerprintedCache<CSharpAnalysisContext>(
            "C# analysis",
            maxEntries,
            ttl,
            timeProvider,
            logger ?? NullLogger<CSharpAnalysisCache>.Instance,
            watcherFactory,
            reuseWhenClean: false);
    }

    internal int CacheEntryCount => _cache.EntryCount;

    internal int CacheLockCount => _cache.LockCount;

    public Task<CSharpAnalysisContext> BuildAsync(string projectPath, CancellationToken cancellationToken = default) =>
        _cache.GetOrBuildAsync(
            projectPath,
            path => TryComputeFingerprint(path, out var fingerprint) ? fingerprint : null,
            ct => _inner.BuildAsync(projectPath, ct),
            (context, stale) => context.Stale = stale,
            cancellationToken);

    public void Dispose() => _cache.Dispose();

    private bool TryComputeFingerprint(string projectPath, out string fingerprint) {
        fingerprint = string.Empty;
        try {
            var files = _filesystem.FindCSharpFiles(projectPath).ToList();
            var projectJson = _filesystem.FindProjectJson(projectPath);
            if (projectJson is not null) {
                files.Add(projectJson);
            }

            // The generated coded-workflow sources are compilation inputs (see
            // CSharpContextBuilder) but are invisible to IFilesystemProvider by design, so
            // they must be stat-ed through the same direct-IO seam the builder uses. Studio
            // regenerates ObjectRepository.cs whenever the Object Repository changes; without
            // its write time in the fingerprint a regenerated descriptor surface would be
            // served from a stale compilation forever.
            var generated = CSharpContextBuilder.FindGeneratedCodedFiles(projectPath)
                .Select(file => (Path: file, Ticks: _resolver.SafeGetWriteTicks(file)));
            var extra = GetPackageFolders(projectJson)
                .Select(folder => (Path: folder, Ticks: _resolver.SafeGetWriteTicks(folder)))
                .Concat(generated);
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
}
