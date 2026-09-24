using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using UiPath.Engineering.Mcp.Core.CodeAnalysis;

namespace UiPath.Engineering.Mcp.Core.Tests;

public class CSharpAnalysisCacheTests {
    private const string Root = "/projects/testProcess";
    private const string Json = "/projects/testProcess/project.json";
    private const string FlowCs = "/projects/testProcess/InvoiceFlow.cs";

    private static int _buildCounter;

    private sealed class CountingContextBuilder : ICSharpContextBuilder {
        public int CallCount { get; private set; }
        public Exception? ToThrow { get; set; }

        public Task<CSharpAnalysisContext> BuildAsync(string projectPath, CancellationToken cancellationToken = default) {
            CallCount++;
            if (ToThrow is not null) {
                return Task.FromException<CSharpAnalysisContext>(ToThrow);
            }
            var compilation = CSharpCompilation.Create(
                $"analysis-build-{++_buildCounter}",
                options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            return Task.FromResult(new CSharpAnalysisContext {
                Compilation = compilation,
                Mode = CSharpAnalysisMode.Full,
                HasCSharpFiles = true
            });
        }
    }

    // Test seam: NuGetReferenceResolver.GetPackagesFolder probes the real disk,
    // so tests substitute a fixed (fake) packages folder.
    private sealed class FixedPackagesFolderResolver : NuGetReferenceResolver {
        private readonly string? _folder;

        public Dictionary<string, DateTime> WriteTimesUtc { get; } = new(StringComparer.OrdinalIgnoreCase);

        public FixedPackagesFolderResolver(string? folder) => _folder = folder;

        public override string? GetPackagesFolder() => _folder;

        public override DateTime GetLastWriteTimeUtc(string path) {
            if (WriteTimesUtc.TryGetValue(path, out var timestamp)) {
                return timestamp;
            }

            throw new DirectoryNotFoundException(path);
        }
    }

    private static FakeFilesystemProvider CreateFilesystem() {
        var fs = new FakeFilesystemProvider { ProjectJsonPath = Json };
        fs.CSharpFiles.Add(FlowCs);
        fs.WriteTimesUtc[Json] = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        fs.WriteTimesUtc[FlowCs] = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        return fs;
    }

    [Fact]
    public async Task BuildAsync_UnchangedFiles_ReturnsCachedContextAndBuildsOnce() {
        var fs = CreateFilesystem();
        var inner = new CountingContextBuilder();
        var sut = new CSharpAnalysisCache(inner, fs, new FixedPackagesFolderResolver(null));

        var first = await sut.BuildAsync(Root);
        var second = await sut.BuildAsync(Root);

        Assert.Equal(1, inner.CallCount);
        Assert.Same(first, second);
    }

    [Fact]
    public async Task BuildAsync_ChangedCSharpTimestamp_TriggersRebuild() {
        var fs = CreateFilesystem();
        var inner = new CountingContextBuilder();
        var sut = new CSharpAnalysisCache(inner, fs, new FixedPackagesFolderResolver(null));

        await sut.BuildAsync(Root);
        fs.WriteTimesUtc[FlowCs] = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var second = await sut.BuildAsync(Root);

        Assert.Equal(2, inner.CallCount);
        Assert.NotSame(await sut.BuildAsync("/projects/other"), second);
    }

    [Fact]
    public async Task BuildAsync_AddedCSharpFile_TriggersRebuild() {
        var fs = CreateFilesystem();
        var inner = new CountingContextBuilder();
        var sut = new CSharpAnalysisCache(inner, fs, new FixedPackagesFolderResolver(null));

        await sut.BuildAsync(Root);
        const string helper = "/projects/testProcess/Helpers.cs";
        fs.CSharpFiles.Add(helper);
        fs.WriteTimesUtc[helper] = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        await sut.BuildAsync(Root);

        Assert.Equal(2, inner.CallCount);
    }

    [Fact]
    public async Task BuildAsync_InnerThrows_ExceptionIsNotCached() {
        var fs = CreateFilesystem();
        var inner = new CountingContextBuilder { ToThrow = new FileNotFoundException("boom") };
        var sut = new CSharpAnalysisCache(inner, fs, new FixedPackagesFolderResolver(null));

        await Assert.ThrowsAsync<FileNotFoundException>(() => sut.BuildAsync(Root));

        inner.ToThrow = null;
        var context = await sut.BuildAsync(Root);

        Assert.Equal(2, inner.CallCount);
        Assert.NotNull(context);
    }

    private static readonly string PackagesFolder = Path.Combine("/", "nuget");
    private static readonly string PackageIdFolder = Path.Combine(PackagesFolder, "uipath.system.activities");
    private static readonly string PackageVersionFolder = Path.Combine(PackageIdFolder, "24.10.4");

    private static FakeFilesystemProvider CreateFilesystemWithDependency() {
        var fs = CreateFilesystem();
        fs.FileContents[Json] = """
            {
              "name": "testProcess",
              "targetFramework": "net6.0",
              "dependencies": { "UiPath.System.Activities": "24.10.4" }
            }
            """;
        return fs;
    }

    [Fact]
    public async Task BuildAsync_RestoredNuGetPackageFolder_TriggersRebuild() {
        // Regression: a `dotnet restore` only changes the machine-global NuGet
        // packages folder (outside the project tree), so it must invalidate the cache.
        var fs = CreateFilesystemWithDependency();
        fs.AllowedRoots = [Root];
        var inner = new CountingContextBuilder();
        var resolver = new FixedPackagesFolderResolver(PackagesFolder);
        var sut = new CSharpAnalysisCache(inner, fs, resolver);

        var first = await sut.BuildAsync(Root);
        Assert.Same(first, await sut.BuildAsync(Root));
        Assert.Equal(1, inner.CallCount);

        // Simulate restore: the package folders appear under the packages folder.
        var restored = new DateTime(2024, 3, 1, 0, 0, 0, DateTimeKind.Utc);
        resolver.WriteTimesUtc[PackageIdFolder] = restored;
        resolver.WriteTimesUtc[PackageVersionFolder] = restored;

        var second = await sut.BuildAsync(Root);

        Assert.Equal(2, inner.CallCount);
        Assert.NotSame(first, second);
    }

    [Fact]
    public async Task BuildAsync_NuGetPackageFolderTimestampChanges_TriggersRebuild() {
        var fs = CreateFilesystemWithDependency();
        fs.AllowedRoots = [Root];
        var stamp = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var resolver = new FixedPackagesFolderResolver(PackagesFolder);
        resolver.WriteTimesUtc[PackageIdFolder] = stamp;
        resolver.WriteTimesUtc[PackageVersionFolder] = stamp;
        var inner = new CountingContextBuilder();
        var sut = new CSharpAnalysisCache(inner, fs, resolver);

        var first = await sut.BuildAsync(Root);
        resolver.WriteTimesUtc[PackageVersionFolder] = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var second = await sut.BuildAsync(Root);

        Assert.Equal(2, inner.CallCount);
        Assert.NotSame(first, second);
    }

    [Fact]
    public async Task BuildAsync_RenamedCSharpSameCountAndTimestamp_TriggersRebuild() {
        var fs = CreateFilesystem();
        var inner = new CountingContextBuilder();
        var sut = new CSharpAnalysisCache(inner, fs, new FixedPackagesFolderResolver(null));

        await sut.BuildAsync(Root);

        fs.CSharpFiles.Remove(FlowCs);
        const string renamed = "/projects/testProcess/InvoiceFlowRenamed.cs";
        fs.CSharpFiles.Add(renamed);
        fs.WriteTimesUtc[renamed] = fs.WriteTimesUtc[FlowCs];
        fs.WriteTimesUtc.Remove(FlowCs);

        await sut.BuildAsync(Root);

        Assert.Equal(2, inner.CallCount);
    }

    [Fact]
    public async Task BuildAsync_FingerprintFailure_ServesCachedContextAsStale() {
        var fs = CreateFilesystem();
        var inner = new CountingContextBuilder();
        var sut = new CSharpAnalysisCache(inner, fs, new FixedPackagesFolderResolver(null));

        var first = await sut.BuildAsync(Root);
        Assert.False(first.Stale);

        fs.GetLastWriteTimeException = new IOException("denied");
        var second = await sut.BuildAsync(Root);

        Assert.Equal(1, inner.CallCount);
        Assert.Same(first, second);
        Assert.True(second.Stale);
    }

    [Fact]
    public async Task BuildAsync_FingerprintRecovered_ClearsStaleFlag() {
        var fs = CreateFilesystem();
        var inner = new CountingContextBuilder();
        var sut = new CSharpAnalysisCache(inner, fs, new FixedPackagesFolderResolver(null));

        await sut.BuildAsync(Root);
        fs.GetLastWriteTimeException = new IOException("denied");
        Assert.True((await sut.BuildAsync(Root)).Stale);

        fs.GetLastWriteTimeException = null;
        var recovered = await sut.BuildAsync(Root);

        Assert.Equal(1, inner.CallCount);
        Assert.False(recovered.Stale);
    }

    [Fact]
    public async Task BuildAsync_ExceedsMaxEntries_EvictsLeastRecentlyUsed() {
        var fs = CreateFilesystem();
        var inner = new CountingContextBuilder();
        var sut = new CSharpAnalysisCache(inner, fs, new FixedPackagesFolderResolver(null), maxEntries: 2);

        await sut.BuildAsync("/projects/one");
        await sut.BuildAsync("/projects/two");
        await sut.BuildAsync("/projects/three");
        await sut.BuildAsync("/projects/one");

        Assert.Equal(4, inner.CallCount);
        Assert.Equal(2, sut.CacheEntryCount);
    }

    [Fact]
    public async Task BuildAsync_IdlePastTtl_Rebuilds() {
        var fs = CreateFilesystem();
        var inner = new CountingContextBuilder();
        var time = new ManualTimeProvider();
        var sut = new CSharpAnalysisCache(
            inner, fs, new FixedPackagesFolderResolver(null), maxEntries: 8, ttl: TimeSpan.FromMinutes(10), timeProvider: time);

        await sut.BuildAsync(Root);
        time.Advance(TimeSpan.FromMinutes(11));
        await sut.BuildAsync(Root);

        Assert.Equal(2, inner.CallCount);
    }

    // --- generated coded-workflow sources (.local/.codedworkflows) --------------
    //
    // Studio regenerates ObjectRepository.cs when the Object Repository changes. It is a
    // compilation input (see CSharpContextBuilder) but invisible to IFilesystemProvider,
    // so its write time has to be stat-ed directly or a regenerated descriptor surface
    // would be served from a stale compilation forever.

    /// <summary>
    /// Like <see cref="FixedPackagesFolderResolver"/> but falls through to the real disk,
    /// so generated files under a temp project are stat-ed as production would.
    /// </summary>
    private sealed class DiskBackedResolver : NuGetReferenceResolver {
        public override string? GetPackagesFolder() => null;
    }

    private sealed class GeneratedSourcesProject : IDisposable {
        public string Root { get; } = Path.Combine(
            Path.GetTempPath(), "cache-generated-" + Guid.NewGuid().ToString("N"));

        public string GeneratedFolder => Path.Combine(Root, ".local", ".codedworkflows");

        public string GeneratedObjectRepository => Path.Combine(GeneratedFolder, "ObjectRepository.cs");

        public FakeFilesystemProvider Filesystem { get; } = new();

        public GeneratedSourcesProject() {
            Directory.CreateDirectory(Root);
            var json = Path.Combine(Root, "project.json");
            var flowCs = Path.Combine(Root, "LoginFlow.cs");
            File.WriteAllText(json, """{ "name": "testProcess", "targetFramework": "net8.0", "dependencies": {} }""");
            File.WriteAllText(flowCs, "namespace TestProcess; public class LoginFlow { }");

            Filesystem.ProjectJsonPath = json;
            Filesystem.CSharpFiles.Add(flowCs);
            var stamp = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            Filesystem.WriteTimesUtc[json] = stamp;
            Filesystem.WriteTimesUtc[flowCs] = stamp;
        }

        public void WriteGeneratedObjectRepository(string app) {
            Directory.CreateDirectory(GeneratedFolder);
            File.WriteAllText(GeneratedObjectRepository, $$"""
                namespace TestProcess;
                public static class Descriptors {
                    public static class {{app}} { public static string Screen => "selector"; }
                }
                """);
        }

        public void SetGeneratedWriteTime(DateTime timestamp) =>
            File.SetLastWriteTimeUtc(GeneratedObjectRepository, timestamp);

        public void Dispose() {
            try { Directory.Delete(Root, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task BuildAsync_GeneratedObjectRepositoryAppears_TriggersRebuild() {
        using var project = new GeneratedSourcesProject();
        var inner = new CountingContextBuilder();
        var sut = new CSharpAnalysisCache(inner, project.Filesystem, new DiskBackedResolver());

        var first = await sut.BuildAsync(project.Root);
        Assert.Same(first, await sut.BuildAsync(project.Root));
        Assert.Equal(1, inner.CallCount);

        project.WriteGeneratedObjectRepository("MyApp");

        var rebuilt = await sut.BuildAsync(project.Root);

        Assert.Equal(2, inner.CallCount);
        Assert.NotSame(first, rebuilt);
    }

    [Fact]
    public async Task BuildAsync_GeneratedObjectRepositoryRegenerated_TriggersRebuild() {
        // Regression: a re-capture rewrites ObjectRepository.cs in place. The file set is
        // identical, so only its write time can invalidate the cached compilation.
        using var project = new GeneratedSourcesProject();
        project.WriteGeneratedObjectRepository("MyApp");
        project.SetGeneratedWriteTime(new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var inner = new CountingContextBuilder();
        var sut = new CSharpAnalysisCache(inner, project.Filesystem, new DiskBackedResolver());

        var first = await sut.BuildAsync(project.Root);
        Assert.Equal(1, inner.CallCount);

        project.SetGeneratedWriteTime(new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc));

        var rebuilt = await sut.BuildAsync(project.Root);

        Assert.Equal(2, inner.CallCount);
        Assert.NotSame(first, rebuilt);
    }

    [Fact]
    public async Task BuildAsync_GeneratedObjectRepositoryUnchanged_ServesCache() {
        using var project = new GeneratedSourcesProject();
        project.WriteGeneratedObjectRepository("MyApp");
        var inner = new CountingContextBuilder();
        var sut = new CSharpAnalysisCache(inner, project.Filesystem, new DiskBackedResolver());

        var first = await sut.BuildAsync(project.Root);
        var second = await sut.BuildAsync(project.Root);

        Assert.Equal(1, inner.CallCount);
        Assert.Same(first, second);
    }

    [Fact]
    public async Task BuildAsync_NoGeneratedSources_FingerprintStillComputes() {
        // Absent `.local` must not degrade to the stale path, and must not silently pull
        // generated files into the authored-source list.
        using var project = new GeneratedSourcesProject();
        var inner = new CountingContextBuilder();
        var sut = new CSharpAnalysisCache(inner, project.Filesystem, new DiskBackedResolver());

        var context = await sut.BuildAsync(project.Root);

        Assert.False(context.Stale);
        Assert.Equal(1, inner.CallCount);
        Assert.All(
            project.Filesystem.CSharpFiles,
            file => Assert.DoesNotContain(".local", file, StringComparison.Ordinal));
    }
}
