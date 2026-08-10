using UiPath.Engineering.Mcp.Core.CodeAnalysis;

namespace UiPath.Engineering.Mcp.Tools.Tests;

public class CodeAnalysisToolsTests {
    private static FakeFilesystemProvider ProjectFilesystem() =>
        new() { Allowed = true, ProjectJson = "/projects/testProcess/project.json" };

    // --- find_code_symbol ---

    [Fact]
    public async Task FindCodeSymbol_PathNotAllowed_ReturnsError() {
        var tool = new FindCodeSymbolTool(new FakeFilesystemProvider { Allowed = false }, new FakeCSharpAnalysisService());

        var result = await tool.FindCodeSymbol("/not/allowed", "Execute");

        Assert.Equal("error", result.Status);
        Assert.Equal("Path not allowed.", result.Summary);
    }

    [Fact]
    public async Task FindCodeSymbol_HappyPath_ReturnsMatchesAndForwardsArguments() {
        var analysis = new FakeCSharpAnalysisService {
            SymbolResult = new FindSymbolResult {
                Matches = [new SymbolMatch { Name = "Execute", Kind = "method", FilePath = "Flow.cs", Line = 6 }]
            }
        };
        var tool = new FindCodeSymbolTool(ProjectFilesystem(), analysis);

        var result = await tool.FindCodeSymbol("/projects/testProcess", "Execute", kind: "method");

        Assert.Equal("success", result.Status);
        Assert.Equal("/projects/testProcess", analysis.LastProjectPath);
        Assert.Equal("Execute", analysis.LastSymbol);
        Assert.Equal("method", analysis.LastKind);
        var data = Assert.IsType<FindSymbolResult>(result.Data);
        Assert.Single(data.Matches);
    }

    [Fact]
    public async Task FindCodeSymbol_ServiceThrows_ReturnsStructuredError() {
        var analysis = new FakeCSharpAnalysisService { ToThrow = new InvalidOperationException("boom") };
        var tool = new FindCodeSymbolTool(ProjectFilesystem(), analysis);

        var result = await tool.FindCodeSymbol("/projects/testProcess", "Execute");

        Assert.Equal("error", result.Status);
        Assert.Contains("boom", result.Errors);
    }

    // --- get_code_context ---

    [Fact]
    public async Task GetCodeContext_PathNotAllowed_ReturnsError() {
        var tool = new GetCodeContextTool(new FakeFilesystemProvider { Allowed = false }, new FakeCSharpAnalysisService());

        var result = await tool.GetCodeContext("/not/allowed", symbol: "Execute");

        Assert.Equal("error", result.Status);
        Assert.Equal("Path not allowed.", result.Summary);
    }

    [Fact]
    public async Task GetCodeContext_BySymbol_ForwardsArguments() {
        var analysis = new FakeCSharpAnalysisService {
            ContextResult = new CodeContextResult { Found = true, Name = "Execute", Signature = "Execute()" }
        };
        var tool = new GetCodeContextTool(ProjectFilesystem(), analysis);

        var result = await tool.GetCodeContext("/projects/testProcess", symbol: "Execute");

        Assert.Equal("success", result.Status);
        Assert.Equal("Execute", analysis.LastSymbol);
        var data = Assert.IsType<CodeContextResult>(result.Data);
        Assert.True(data.Found);
    }

    [Fact]
    public async Task GetCodeContext_ByFileAndLine_ForwardsArguments() {
        var analysis = new FakeCSharpAnalysisService();
        var tool = new GetCodeContextTool(ProjectFilesystem(), analysis);

        await tool.GetCodeContext("/projects/testProcess", file: "Flow.cs", line: 6);

        Assert.Equal("Flow.cs", analysis.LastFile);
        Assert.Equal(6, analysis.LastLine);
    }
}
