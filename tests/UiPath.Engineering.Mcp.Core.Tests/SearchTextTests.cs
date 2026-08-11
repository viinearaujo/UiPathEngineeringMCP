using UiPath.Engineering.Mcp.Core.CodeAnalysis;
using UiPath.Engineering.Mcp.Core.CodeSearch;
using UiPath.Engineering.Mcp.Core.Models;
using UiPath.Engineering.Mcp.Core.Parsing;

namespace UiPath.Engineering.Mcp.Core.Tests;

public class SearchTextTests {
    private const string Root = "/projects/testProcess";
    private const string MainXaml = "/projects/testProcess/Main.xaml";
    private const string FlowCs = "/projects/testProcess/InvoiceFlow.cs";

    private sealed class StubContextBuilder : ICSharpContextBuilder {
        public Task<CSharpAnalysisContext> BuildAsync(string projectPath, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class StubProjectModelBuilder : IProjectModelBuilder {
        public Task<UiPathProjectModel> BuildAsync(string projectPath, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private static CodebaseSearchService CreateService(FakeFilesystemProvider fs) =>
        new(new StubContextBuilder(), new StubProjectModelBuilder(), fs);

    private static FakeFilesystemProvider CreateFilesystem() {
        var fs = new FakeFilesystemProvider();
        fs.XamlFiles.Add(MainXaml);
        fs.CSharpFiles.Add(FlowCs);
        fs.FileContents[MainXaml] = "<Sequence DisplayName=\"Dequeue item\"><WriteLine /></Sequence>";
        fs.FileContents[FlowCs] = "public class InvoiceFlow {\n    public object GetQueueItem() { return null; }\n}";
        return fs;
    }

    [Fact]
    public async Task SearchText_CaseInsensitiveSubstring_MatchesAcrossXamlAndCs() {
        var sut = CreateService(CreateFilesystem());

        var result = await sut.SearchTextAsync(Root, "queue");

        Assert.Equal(2, result.FilesSearched);
        Assert.Equal(2, result.Matches.Count);
        Assert.Contains(result.Matches, m => m.FilePath == MainXaml && m.Line == 1);
        Assert.Contains(result.Matches, m => m.FilePath == FlowCs && m.Line == 2);
    }

    [Fact]
    public async Task SearchText_ExactCaseMatches_OrderBeforeCaseInsensitiveOnly() {
        var fs = new FakeFilesystemProvider();
        fs.XamlFiles.Add(MainXaml);
        fs.CSharpFiles.Add(FlowCs);
        // The case-sensitive hit lives in the file that sorts SECOND by path,
        // so only tier-based ordering puts it first.
        fs.FileContents[MainXaml] = "logger.Info(\"starting\")";
        fs.FileContents[FlowCs] = "Log(\"starting\");";
        var sut = CreateService(fs);

        var result = await sut.SearchTextAsync(Root, "Log");

        Assert.Equal(2, result.Matches.Count);
        Assert.Equal(FlowCs, result.Matches[0].FilePath); // exact case-sensitive substring
        Assert.Equal(MainXaml, result.Matches[1].FilePath); // case-insensitive only
    }

    [Fact]
    public async Task SearchText_SnippetTrimmedAndCappedAt300Chars() {
        var fs = new FakeFilesystemProvider();
        fs.XamlFiles.Add(MainXaml);
        fs.FileContents[MainXaml] = "   " + new string('x', 400) + " queue " + new string('y', 100);
        var sut = CreateService(fs);

        var result = await sut.SearchTextAsync(Root, "queue");

        var match = Assert.Single(result.Matches);
        Assert.Equal(301, match.Snippet.Length); // 300 chars + ellipsis
        Assert.EndsWith("…", match.Snippet);
        Assert.False(match.Snippet.StartsWith(' '));
    }

    [Fact]
    public async Task SearchText_OversizedFile_SkippedWithWarning() {
        var fs = new FakeFilesystemProvider();
        fs.XamlFiles.Add(MainXaml);
        fs.CSharpFiles.Add(FlowCs);
        fs.FileContents[MainXaml] = new string('x', 2_000_001); // over the 2 MB guard
        fs.FileContents[FlowCs] = "var queue = 1;";
        var sut = CreateService(fs);

        var result = await sut.SearchTextAsync(Root, "queue");

        Assert.Equal([MainXaml], result.SkippedFiles);
        Assert.Single(result.Warnings);
        Assert.Equal(1, result.FilesSearched);
        Assert.Single(result.Matches);
    }

    [Fact]
    public async Task SearchText_UnreadableFile_SkippedWithWarning() {
        var fs = new FakeFilesystemProvider();
        fs.XamlFiles.Add(MainXaml); // no FileContents entry -> ReadAllText throws FileNotFoundException
        fs.CSharpFiles.Add(FlowCs);
        fs.FileContents[FlowCs] = "var queue = 1;";
        var sut = CreateService(fs);

        var result = await sut.SearchTextAsync(Root, "queue");

        Assert.Equal([MainXaml], result.SkippedFiles);
        Assert.Single(result.Warnings);
        Assert.Single(result.Matches);
    }

    [Fact]
    public async Task SearchText_MoreThan200Matches_Truncated() {
        var fs = new FakeFilesystemProvider();
        fs.XamlFiles.Add(MainXaml);
        fs.FileContents[MainXaml] = string.Join('\n', Enumerable.Repeat("queue", 210));
        var sut = CreateService(fs);

        var result = await sut.SearchTextAsync(Root, "queue");

        Assert.Equal(200, result.Matches.Count);
        Assert.True(result.Truncated);
        Assert.Contains("truncated", result.Note);
    }
}
