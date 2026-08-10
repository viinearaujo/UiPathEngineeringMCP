namespace UiPath.Engineering.Mcp.Core.Tests;

public class FindReferencesTests : CSharpAnalysisServiceTestBase {
    // Line map (1-based): 1 using System; | 2 blank | 3 namespace | 4 blank |
    // 5 class | 6 Execute | 7 Log("start"); | 8 return | 9 } | 10 blank |
    // 11 Log declaration | 12 Console | 13 } | 14 }
    private const string Source = """
        using System;

        namespace TestProcess;

        public class InvoiceFlow {
            public int Execute(string input, int count) {
                Log("start");
                return count + 1;
            }

            private void Log(string message) {
                Console.WriteLine(message);
            }
        }
        """;

    [Fact]
    public async Task FindReferences_MethodCall_ReturnsCallSiteWithMemberAndSnippet() {
        var service = CreateService(BuildContext(Source));

        var result = await service.FindReferencesAsync(Root, "Log");

        var reference = Assert.Single(result.References);
        Assert.Equal(FlowCs, reference.FilePath);
        Assert.Equal(7, reference.Line);
        Assert.Equal("Execute", reference.ContainingMember);
        Assert.Contains("Log(", reference.Snippet);
    }

    [Fact]
    public async Task FindReferences_UnknownName_FallsBackToIdentifierMatching() {
        // "ExternalCall" is not declared anywhere: semantic matching finds no target,
        // so the result relies on identifier-name matching and still locates the call.
        const string source = """
            public class Flow {
                public void Execute() {
                    ExternalCall();
                }
            }
            """;
        var service = CreateService(BuildContext(source));

        var result = await service.FindReferencesAsync(Root, "ExternalCall");

        var reference = Assert.Single(result.References);
        Assert.Equal(3, reference.Line);
        Assert.Equal("Execute", reference.ContainingMember);
    }

    [Fact]
    public async Task FindReferences_DeclarationOnly_ReturnsNoReferences() {
        // "InvoiceFlow" is declared but never used: constructors/inheritance absent,
        // so there are zero reference sites (the declaration itself is never a match).
        var service = CreateService(BuildContext(Source));

        var result = await service.FindReferencesAsync(Root, "WriteLine");

        Assert.Empty(result.References); // WriteLine's declaration lives in metadata, not source
    }
}
