namespace UiPath.Engineering.Mcp.Core.Tests;

public class GetCodeContextTests : CSharpAnalysisServiceTestBase {
    // Line map (1-based): 1 namespace | 2 blank | 3 class InvoiceFlow | 4 Execute |
    // 5 var helper | 6 helper.Prepare | 7 return | 8 } | 9 } | 10 class Invoice |
    // 11 Total | 12 } | 13 class InvoiceHelper | 14 Prepare | 15 } | 16 }
    private const string Source = """
        namespace TestProcess;

        public class InvoiceFlow {
            public int Execute(Invoice invoice) {
                var helper = new InvoiceHelper();
                helper.Prepare(invoice);
                return invoice.Total;
            }
        }

        public class Invoice {
            public int Total { get; set; }
        }

        public class InvoiceHelper {
            public void Prepare(Invoice invoice) { }
        }
        """;

    [Fact]
    public async Task GetCodeContext_BySymbol_ReturnsMemberContext() {
        var service = CreateService(BuildContext(Source));

        var result = await service.GetCodeContextAsync(Root, symbol: "Execute");

        Assert.True(result.Found);
        Assert.Equal("Execute", result.Name);
        Assert.Equal("method", result.Kind);
        Assert.Equal(FlowCs, result.FilePath);
        Assert.Equal(4, result.Line);
        Assert.Equal("TestProcess.InvoiceFlow", result.ContainingType);
        Assert.Contains("Invoice", result.Signature);
        Assert.Contains("InvoiceHelper.Prepare", result.CalledMethods);
        Assert.Contains("Invoice", result.ReferencedTypes);
        Assert.Contains("InvoiceHelper", result.ReferencedTypes);
        Assert.Contains("helper.Prepare(invoice);", result.Source);
        Assert.False(result.Truncated);
    }

    [Fact]
    public async Task GetCodeContext_ByFileAndLine_ReturnsEnclosingMember() {
        var service = CreateService(BuildContext(Source));

        var result = await service.GetCodeContextAsync(Root, file: FlowCs, line: 6);

        Assert.True(result.Found);
        Assert.Equal("Execute", result.Name);
    }

    [Fact]
    public async Task GetCodeContext_UnknownSymbol_ReturnsFoundFalseWithNote() {
        var service = CreateService(BuildContext(Source));

        var result = await service.GetCodeContextAsync(Root, symbol: "Missing");

        Assert.False(result.Found);
        Assert.NotNull(result.Note);
    }

    [Fact]
    public async Task GetCodeContext_NoArguments_ReturnsFoundFalseWithNote() {
        var service = CreateService(BuildContext(Source));

        var result = await service.GetCodeContextAsync(Root);

        Assert.False(result.Found);
        Assert.NotNull(result.Note);
    }
}
