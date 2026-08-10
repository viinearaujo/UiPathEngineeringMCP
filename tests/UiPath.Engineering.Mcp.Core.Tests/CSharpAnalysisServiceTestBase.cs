using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using UiPath.Engineering.Mcp.Core.CodeAnalysis;

namespace UiPath.Engineering.Mcp.Core.Tests;

/// <summary>
/// Shared helpers for CSharpAnalysisService tests: builds a real Roslyn compilation
/// from source text against the test runtime's assemblies (always resolvable on the
/// build machine) and serves it through a stub ICSharpContextBuilder.
/// </summary>
public abstract class CSharpAnalysisServiceTestBase {
    protected const string Root = "/projects/testProcess";
    protected const string FlowCs = "/projects/testProcess/InvoiceFlow.cs";

    protected static CSharpAnalysisContext BuildContext(
        string source,
        CSharpAnalysisMode mode = CSharpAnalysisMode.Full,
        string filePath = FlowCs,
        bool withRuntimeReferences = true,
        IReadOnlyList<string>? unresolved = null) {
        var tree = CSharpSyntaxTree.ParseText(source, path: filePath);
        List<MetadataReference> references = withRuntimeReferences
            ? Directory.GetFiles(Path.GetDirectoryName(typeof(object).Assembly.Location)!, "*.dll")
                .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
                .ToList()
            : [];
        var compilation = CSharpCompilation.Create(
            "analysis-test",
            [tree],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        return new CSharpAnalysisContext {
            Compilation = compilation,
            Mode = mode,
            UnresolvedReferences = unresolved ?? [],
            HasCSharpFiles = true
        };
    }

    protected static CSharpAnalysisService CreateService(CSharpAnalysisContext context) =>
        new(new StubContextBuilder(context));

    private sealed class StubContextBuilder(CSharpAnalysisContext context) : ICSharpContextBuilder {
        public Task<CSharpAnalysisContext> BuildAsync(string projectPath, CancellationToken cancellationToken = default) =>
            Task.FromResult(context);
    }
}
