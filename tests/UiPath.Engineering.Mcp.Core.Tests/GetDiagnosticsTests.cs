using UiPath.Engineering.Mcp.Core.CodeAnalysis;

namespace UiPath.Engineering.Mcp.Core.Tests;

public class GetDiagnosticsTests : CSharpAnalysisServiceTestBase {
    // Line map (1-based): 1 class Broken | 2 Execute | 3 return missingName | 4 } | 5 }
    private const string BrokenSource = """
        public class Broken {
            public int Execute() {
                return missingName + 1;
            }
        }
        """;

    [Fact]
    public async Task GetDiagnostics_UndefinedIdentifier_ReturnsCs0103() {
        var service = CreateService(BuildContext(BrokenSource));

        var result = await service.GetDiagnosticsAsync(Root);

        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("CS0103", diagnostic.Code);
        Assert.Equal("error", diagnostic.Severity);
        Assert.Equal(3, diagnostic.Line);
        Assert.True(diagnostic.Column > 0);
        Assert.Equal(FlowCs, diagnostic.FilePath);
        Assert.Contains("missingName", diagnostic.Message);
    }

    [Fact]
    public async Task GetDiagnostics_CleanSource_ReturnsEmpty() {
        const string source = """
            public class Clean {
                public int Execute() {
                    return 1 + 1;
                }
            }
            """;
        var service = CreateService(BuildContext(source));

        var result = await service.GetDiagnosticsAsync(Root);

        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public async Task GetDiagnostics_PartialMode_SuppressesMissingReferenceNoise() {
        // No runtime references at all: 'Missing.Thing' yields CS0246 (among other noise).
        const string source = """
            public class Uses {
                public Missing.Thing Make() => new Missing.Thing();
            }
            """;
        var context = BuildContext(source, mode: CSharpAnalysisMode.Partial,
            withRuntimeReferences: false, unresolved: ["Missing.Package"]);
        var service = CreateService(context);

        var result = await service.GetDiagnosticsAsync(Root);

        Assert.DoesNotContain(result.Diagnostics, d => d.Code == "CS0246");
        Assert.True(result.SuppressedMissingReferenceDiagnostics >= 1);
        Assert.NotNull(result.Note);
        Assert.Equal("partial", result.AnalysisMode);
    }

    [Fact]
    public async Task GetDiagnostics_SyntaxOnlyMode_ReturnsEmptyWithNote() {
        var context = BuildContext(BrokenSource, mode: CSharpAnalysisMode.SyntaxOnly);
        var service = CreateService(context);

        var result = await service.GetDiagnosticsAsync(Root);

        Assert.Empty(result.Diagnostics);
        Assert.NotNull(result.Note);
        Assert.Equal("syntaxOnly", result.AnalysisMode);
    }
}
