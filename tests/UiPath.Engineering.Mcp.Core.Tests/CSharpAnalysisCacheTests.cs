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
        var sut = new CSharpAnalysisCache(inner, fs);

        var first = await sut.BuildAsync(Root);
        var second = await sut.BuildAsync(Root);

        Assert.Equal(1, inner.CallCount);
        Assert.Same(first, second);
    }

    [Fact]
    public async Task BuildAsync_ChangedCSharpTimestamp_TriggersRebuild() {
        var fs = CreateFilesystem();
        var inner = new CountingContextBuilder();
        var sut = new CSharpAnalysisCache(inner, fs);

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
        var sut = new CSharpAnalysisCache(inner, fs);

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
        var sut = new CSharpAnalysisCache(inner, fs);

        await Assert.ThrowsAsync<FileNotFoundException>(() => sut.BuildAsync(Root));

        inner.ToThrow = null;
        var context = await sut.BuildAsync(Root);

        Assert.Equal(2, inner.CallCount);
        Assert.NotNull(context);
    }
}
