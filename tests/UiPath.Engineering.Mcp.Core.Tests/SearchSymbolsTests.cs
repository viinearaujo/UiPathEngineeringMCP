using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using UiPath.Engineering.Mcp.Core.CodeAnalysis;
using UiPath.Engineering.Mcp.Core.CodeSearch;
using UiPath.Engineering.Mcp.Core.Models;
using UiPath.Engineering.Mcp.Core.Parsing;

namespace UiPath.Engineering.Mcp.Core.Tests;

public class SearchSymbolsTests : CSharpAnalysisServiceTestBase {
    private const string Source = """
        namespace TestProcess;

        public class InvoiceFlow {
            public string QueueName { get; set; }
            public int Execute(string input) { return 1; }
            public int ExecuteAsync(string input) { return 2; }
            public void Log(string message) { }
            public void LogMessage(string message) { }
        }
        """;

    private sealed class StubContextBuilder(CSharpAnalysisContext context) : ICSharpContextBuilder {
        public Task<CSharpAnalysisContext> BuildAsync(string projectPath, CancellationToken cancellationToken = default) =>
            Task.FromResult(context);
    }

    private sealed class StubProjectModelBuilder : IProjectModelBuilder {
        public Task<UiPathProjectModel> BuildAsync(string projectPath, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    // Named CreateSearchService (not CreateService) to avoid hiding the base
    // class's CSharpAnalysisService factory.
    private static CodebaseSearchService CreateSearchService(CSharpAnalysisContext context) =>
        new(new StubContextBuilder(context), new StubProjectModelBuilder(), new FakeFilesystemProvider());

    [Fact]
    public async Task SearchSymbols_SubstringMatch_FindsMethodsCaseInsensitively() {
        var sut = CreateSearchService(BuildContext(Source));

        var result = await sut.SearchSymbolsAsync(Root, "execute");

        Assert.Equal(2, result.Matches.Count);
        Assert.All(result.Matches, m => Assert.Equal("method", m.Kind));
        Assert.All(result.Matches, m => Assert.Equal(FlowCs, m.FilePath));
        Assert.Equal("full", result.AnalysisMode);
    }

    [Fact]
    public async Task SearchSymbols_KindFilter_NarrowsMatches() {
        var sut = CreateSearchService(BuildContext(Source));

        var methods = await sut.SearchSymbolsAsync(Root, "invoice", kind: "class");
        var properties = await sut.SearchSymbolsAsync(Root, "queue", kind: "property");
        var wrongKind = await sut.SearchSymbolsAsync(Root, "queue", kind: "method");

        var type = Assert.Single(methods.Matches);
        Assert.Equal("class", type.Kind);
        Assert.Equal("InvoiceFlow", type.Name);
        var property = Assert.Single(properties.Matches);
        Assert.Equal("QueueName", property.Name);
        Assert.Empty(wrongKind.Matches);
    }

    [Fact]
    public async Task SearchSymbols_ExactNameMatch_OrdersFirst() {
        var sut = CreateSearchService(BuildContext(Source));

        var result = await sut.SearchSymbolsAsync(Root, "Log");

        Assert.Equal(2, result.Matches.Count);
        Assert.Equal("Log", result.Matches[0].Name); // exact ordinal-name equality
        Assert.Equal("LogMessage", result.Matches[1].Name);
    }

    [Fact]
    public async Task SearchSymbols_PartialMode_CarriesTransparencyFields() {
        var context = BuildContext(Source, mode: CSharpAnalysisMode.Partial, unresolved: ["UiPath.System.Activities"]);
        var sut = CreateSearchService(context);

        var result = await sut.SearchSymbolsAsync(Root, "Execute");

        Assert.Equal("partial", result.AnalysisMode);
        Assert.Equal(["UiPath.System.Activities"], result.UnresolvedReferences);
        Assert.NotEmpty(result.Matches); // source symbols still resolve in degraded modes
    }

    [Fact]
    public async Task SearchSymbols_NoCSharpFiles_NotesAndReturnsEmpty() {
        var context = new CSharpAnalysisContext {
            Compilation = CSharpCompilation.Create(
                "analysis-empty",
                options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)),
            Mode = CSharpAnalysisMode.Full,
            HasCSharpFiles = false
        };
        var sut = CreateSearchService(context);

        var result = await sut.SearchSymbolsAsync(Root, "Execute");

        Assert.False(result.HasCSharpFiles);
        Assert.Empty(result.Matches);
        Assert.Equal("The project contains no C# files.", result.Note);
    }

    [Fact]
    public async Task SearchSymbols_CancelledToken_ThrowsDuringEnumeration() {
        var sut = CreateSearchService(BuildContext(Source));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => sut.SearchSymbolsAsync(Root, "execute", cancellationToken: cts.Token));
    }

    [Fact]
    public async Task SearchSymbols_MoreThan200Matches_Truncated() {
        var members = string.Join("\n", Enumerable.Range(0, 210).Select(i => $"    public void Log{i}() {{ }}"));
        var source = $"public class Bulk {{\n{members}\n}}";
        var sut = CreateSearchService(BuildContext(source));

        var result = await sut.SearchSymbolsAsync(Root, "Log");

        Assert.Equal(200, result.Matches.Count);
        Assert.True(result.Truncated);
        Assert.Contains("truncated", result.Note);
    }
}
