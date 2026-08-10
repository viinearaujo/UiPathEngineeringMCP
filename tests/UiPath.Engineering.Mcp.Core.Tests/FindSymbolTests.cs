using UiPath.Engineering.Mcp.Core.CodeAnalysis;

namespace UiPath.Engineering.Mcp.Core.Tests;

public class FindSymbolTests : CSharpAnalysisServiceTestBase {
    // Line map (1-based): 1 using System; | 2 blank | 3 namespace | 4 blank |
    // 5 class InvoiceFlow | 6 Execute | 7 return | 8 } | 9 blank | 10 Log | 11 Console | 12 } | 13 }
    private const string Source = """
        using System;

        namespace TestProcess;

        public class InvoiceFlow {
            public int Execute(string input, int count) {
                return count + 1;
            }

            private void Log(string message) {
                Console.WriteLine(message);
            }
        }
        """;

    [Fact]
    public async Task FindSymbol_Method_ReturnsMatchWithLocationAndSignature() {
        var service = CreateService(BuildContext(Source));

        var result = await service.FindSymbolAsync(Root, "Execute");

        var match = Assert.Single(result.Matches);
        Assert.Equal("Execute", match.Name);
        Assert.Equal("method", match.Kind);
        Assert.Equal(FlowCs, match.FilePath);
        Assert.Equal(6, match.Line);
        Assert.Equal("TestProcess.InvoiceFlow", match.ContainingType);
        Assert.Contains("Execute", match.Signature);
        Assert.Equal("full", result.AnalysisMode);
    }

    [Fact]
    public async Task FindSymbol_KindFilter_ExcludesNonMatchingKinds() {
        var service = CreateService(BuildContext(Source));

        var methods = await service.FindSymbolAsync(Root, "InvoiceFlow", kind: "method");
        var classes = await service.FindSymbolAsync(Root, "InvoiceFlow", kind: "class");

        Assert.Empty(methods.Matches);
        var match = Assert.Single(classes.Matches);
        Assert.Equal("class", match.Kind);
        Assert.Equal(5, match.Line);
    }

    [Fact]
    public async Task FindSymbol_UnknownName_ReturnsEmptyMatches() {
        var service = CreateService(BuildContext(Source));

        var result = await service.FindSymbolAsync(Root, "DoesNotExist");

        Assert.Empty(result.Matches);
    }

    [Fact]
    public async Task FindSymbol_PartialMode_ReportsModeAndUnresolvedReferences() {
        var context = BuildContext(Source, mode: CSharpAnalysisMode.Partial, unresolved: ["UiPath.System.Activities"]);
        var service = CreateService(context);

        var result = await service.FindSymbolAsync(Root, "Execute");

        Assert.Equal("partial", result.AnalysisMode);
        Assert.Equal(["UiPath.System.Activities"], result.UnresolvedReferences);
        Assert.Single(result.Matches); // declared symbols still resolve in partial mode
    }
}
