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

        public FixedPackagesFolderResolver(string? folder) => _folder = folder;

        public override string? GetPackagesFolder() => _folder;
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
        var inner = new CountingContextBuilder();
        var sut = new CSharpAnalysisCache(inner, fs, new FixedPackagesFolderResolver(PackagesFolder));

        var first = await sut.BuildAsync(Root);
        Assert.Same(first, await sut.BuildAsync(Root));
        Assert.Equal(1, inner.CallCount);

        // Simulate restore: the package folders appear under the packages folder.
        var restored = new DateTime(2024, 3, 1, 0, 0, 0, DateTimeKind.Utc);
        fs.WriteTimesUtc[PackageIdFolder] = restored;
        fs.WriteTimesUtc[PackageVersionFolder] = restored;

        var second = await sut.BuildAsync(Root);

        Assert.Equal(2, inner.CallCount);
        Assert.NotSame(first, second);
    }

    [Fact]
    public async Task BuildAsync_NuGetPackageFolderTimestampChanges_TriggersRebuild() {
        var fs = CreateFilesystemWithDependency();
        var stamp = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        fs.WriteTimesUtc[PackageIdFolder] = stamp;
        fs.WriteTimesUtc[PackageVersionFolder] = stamp;
        var inner = new CountingContextBuilder();
        var sut = new CSharpAnalysisCache(inner, fs, new FixedPackagesFolderResolver(PackagesFolder));

        var first = await sut.BuildAsync(Root);
        fs.WriteTimesUtc[PackageVersionFolder] = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc);
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
}
