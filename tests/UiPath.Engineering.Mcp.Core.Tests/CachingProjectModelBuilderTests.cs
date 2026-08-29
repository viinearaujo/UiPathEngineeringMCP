using UiPath.Engineering.Mcp.Core.Models;
using UiPath.Engineering.Mcp.Core.Parsing;

namespace UiPath.Engineering.Mcp.Core.Tests;

public class CachingProjectModelBuilderTests {
    private const string Root = "/projects/testProcess";
    private const string Json = "/projects/testProcess/project.json";
    private const string MainXaml = "/projects/testProcess/Main.xaml";

    private sealed class CountingProjectModelBuilder : IProjectModelBuilder {
        public int CallCount { get; private set; }
        public Exception? ToThrow { get; set; }

        public Task<UiPathProjectModel> BuildAsync(string projectPath, CancellationToken cancellationToken = default) {
            CallCount++;
            if (ToThrow is not null) {
                return Task.FromException<UiPathProjectModel>(ToThrow);
            }

            return Task.FromResult(new UiPathProjectModel { ProjectName = $"build-{CallCount}" });
        }
    }

    private static FakeFilesystemProvider CreateFilesystem() {
        var fs = new FakeFilesystemProvider { ProjectJsonPath = Json };
        fs.XamlFiles.Add(MainXaml);
        fs.WriteTimesUtc[Json] = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        fs.WriteTimesUtc[MainXaml] = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        return fs;
    }

    [Fact]
    public async Task BuildAsync_UnchangedFiles_ReturnsCachedModelAndBuildsOnce() {
        var fs = CreateFilesystem();
        var inner = new CountingProjectModelBuilder();
        var sut = new CachingProjectModelBuilder(inner, fs);

        var first = await sut.BuildAsync(Root);
        var second = await sut.BuildAsync(Root);

        Assert.Equal(1, inner.CallCount);
        Assert.Same(first, second);
    }

    [Fact]
    public async Task BuildAsync_ChangedFileTimestamp_TriggersRebuild() {
        var fs = CreateFilesystem();
        var inner = new CountingProjectModelBuilder();
        var sut = new CachingProjectModelBuilder(inner, fs);

        await sut.BuildAsync(Root);
        fs.WriteTimesUtc[MainXaml] = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var second = await sut.BuildAsync(Root);

        Assert.Equal(2, inner.CallCount);
        Assert.Equal("build-2", second.ProjectName);
    }

    [Fact]
    public async Task BuildAsync_AddedOrRemovedXamlFile_TriggersRebuild() {
        var fs = CreateFilesystem();
        var inner = new CountingProjectModelBuilder();
        var sut = new CachingProjectModelBuilder(inner, fs);

        await sut.BuildAsync(Root);

        // Add a file.
        const string subXaml = "/projects/testProcess/Sub.xaml";
        fs.XamlFiles.Add(subXaml);
        fs.WriteTimesUtc[subXaml] = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        await sut.BuildAsync(Root);
        Assert.Equal(2, inner.CallCount);

        // Remove it again (count and max timestamp change back -> rebuild).
        fs.XamlFiles.Remove(subXaml);
        fs.WriteTimesUtc.Remove(subXaml);
        await sut.BuildAsync(Root);
        Assert.Equal(3, inner.CallCount);
    }

    [Fact]
    public async Task BuildAsync_ChangedCSharpFileTimestamp_TriggersRebuild() {
        var fs = CreateFilesystem();
        const string coded = "/projects/testProcess/InvoiceFlow.cs";
        fs.CSharpFiles.Add(coded);
        fs.WriteTimesUtc[coded] = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var inner = new CountingProjectModelBuilder();
        var sut = new CachingProjectModelBuilder(inner, fs);

        await sut.BuildAsync(Root);
        fs.WriteTimesUtc[coded] = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var second = await sut.BuildAsync(Root);

        Assert.Equal(2, inner.CallCount);
        Assert.Equal("build-2", second.ProjectName);
    }

    [Fact]
    public async Task BuildAsync_AddedCSharpFile_TriggersRebuild() {
        var fs = CreateFilesystem();
        var inner = new CountingProjectModelBuilder();
        var sut = new CachingProjectModelBuilder(inner, fs);

        await sut.BuildAsync(Root);

        const string coded = "/projects/testProcess/Helpers.cs";
        fs.CSharpFiles.Add(coded);
        fs.WriteTimesUtc[coded] = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        await sut.BuildAsync(Root);

        Assert.Equal(2, inner.CallCount);
    }

    [Fact]
    public async Task BuildAsync_DifferentProjectPaths_AreCachedIndependently() {
        var fs = CreateFilesystem();
        var inner = new CountingProjectModelBuilder();
        var sut = new CachingProjectModelBuilder(inner, fs);

        var first = await sut.BuildAsync("/projects/one");
        var second = await sut.BuildAsync("/projects/two");
        var firstAgain = await sut.BuildAsync("/projects/one");

        Assert.Equal(2, inner.CallCount);
        Assert.NotSame(first, second);
        Assert.Same(first, firstAgain);
    }

    [Fact]
    public async Task BuildAsync_InnerBuilderThrows_ExceptionIsNotCached() {
        var fs = CreateFilesystem();
        var inner = new CountingProjectModelBuilder { ToThrow = new FileNotFoundException("boom") };
        var sut = new CachingProjectModelBuilder(inner, fs);

        await Assert.ThrowsAsync<FileNotFoundException>(() => sut.BuildAsync(Root));

        inner.ToThrow = null;
        var model = await sut.BuildAsync(Root);

        Assert.Equal(2, inner.CallCount);
        Assert.Equal("build-2", model.ProjectName);
    }

    [Fact]
    public async Task BuildAsync_RenamedXamlSameCountAndTimestamp_TriggersRebuild() {
        var fs = CreateFilesystem();
        var inner = new CountingProjectModelBuilder();
        var sut = new CachingProjectModelBuilder(inner, fs);

        await sut.BuildAsync(Root);

        fs.XamlFiles.Remove(MainXaml);
        const string renamed = "/projects/testProcess/MainRenamed.xaml";
        fs.XamlFiles.Add(renamed);
        fs.WriteTimesUtc[renamed] = fs.WriteTimesUtc[MainXaml];
        fs.WriteTimesUtc.Remove(MainXaml);

        await sut.BuildAsync(Root);

        Assert.Equal(2, inner.CallCount);
    }
}
