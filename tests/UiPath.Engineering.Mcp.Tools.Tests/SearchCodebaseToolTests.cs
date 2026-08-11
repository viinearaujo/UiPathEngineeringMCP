using UiPath.Engineering.Mcp.Core;
using UiPath.Engineering.Mcp.Core.CodeAnalysis;
using UiPath.Engineering.Mcp.Core.CodeSearch;

namespace UiPath.Engineering.Mcp.Tools.Tests;

public class SearchCodebaseToolTests {
    private static FakeFilesystemProvider ProjectFilesystem() =>
        new() { Allowed = true, ProjectJson = "/projects/testProcess/project.json" };

    [Fact]
    public async Task SearchCodebase_PathNotAllowed_ReturnsError() {
        var tool = new SearchCodebaseTool(new FakeFilesystemProvider { Allowed = false }, new FakeCodebaseSearchService());

        var result = await tool.SearchCodebase("/not/allowed", "queue", "text");

        Assert.Equal("error", result.Status);
        Assert.Equal("Path not allowed.", result.Summary);
    }

    [Fact]
    public async Task SearchCodebase_ProjectJsonMissing_ReturnsError() {
        var tool = new SearchCodebaseTool(new FakeFilesystemProvider { Allowed = true, ProjectJson = null }, new FakeCodebaseSearchService());

        var result = await tool.SearchCodebase("/projects/testProcess", "queue", "text");

        Assert.Equal("error", result.Status);
        Assert.Equal("project.json not found.", result.Summary);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task SearchCodebase_BlankQuery_ReturnsInvalidArgument(string query) {
        var tool = new SearchCodebaseTool(ProjectFilesystem(), new FakeCodebaseSearchService());

        var result = await tool.SearchCodebase("/projects/testProcess", query, "text");

        Assert.Equal("error", result.Status);
        Assert.Contains(result.ErrorDetails!, e => e.ErrorCode == ToolErrorCodes.InvalidArgument);
    }

    [Fact]
    public async Task SearchCodebase_UnknownMode_ReturnsInvalidArgumentListingModes() {
        var tool = new SearchCodebaseTool(ProjectFilesystem(), new FakeCodebaseSearchService());

        var result = await tool.SearchCodebase("/projects/testProcess", "queue", "semantic");

        Assert.Equal("error", result.Status);
        var error = Assert.Single(result.ErrorDetails!, e => e.ErrorCode == ToolErrorCodes.InvalidArgument);
        Assert.Contains("text", error.Message);
        Assert.Contains("symbol", error.Message);
        Assert.Contains("activity", error.Message);
        Assert.Contains("workflow", error.Message);
    }

    [Fact]
    public async Task SearchCodebase_TextMode_DispatchesAndSummarizes() {
        var search = new FakeCodebaseSearchService {
            TextResult = new TextSearchResult {
                Matches = [new TextMatch { FilePath = "Main.xaml", Line = 3, Snippet = "queue" }],
                FilesSearched = 2
            }
        };
        var tool = new SearchCodebaseTool(ProjectFilesystem(), search);

        var result = await tool.SearchCodebase("/projects/testProcess", "queue", "text");

        Assert.Equal("success", result.Status);
        Assert.Equal("/projects/testProcess", search.LastProjectPath);
        Assert.Equal("queue", search.LastQuery);
        Assert.Contains("1 text match(es)", result.Summary);
        Assert.IsType<TextSearchResult>(result.Data);
    }

    [Fact]
    public async Task SearchCodebase_SymbolMode_ForwardsKindCaseInsensitively() {
        var search = new FakeCodebaseSearchService {
            SymbolResult = new SymbolSearchResult {
                Matches = [new SymbolMatch { Name = "Execute", Kind = "method", FilePath = "Flow.cs", Line = 6 }]
            }
        };
        var tool = new SearchCodebaseTool(ProjectFilesystem(), search);

        var result = await tool.SearchCodebase("/projects/testProcess", "Execute", "Symbol", kind: "method");

        Assert.Equal("success", result.Status);
        Assert.Equal("method", search.LastKind);
        Assert.IsType<SymbolSearchResult>(result.Data);
    }

    [Fact]
    public async Task SearchCodebase_ActivityMode_Dispatches() {
        var search = new FakeCodebaseSearchService {
            ActivityResult = new ActivitySearchResult {
                Matches = [new ActivityMatch { WorkflowFile = "Main.xaml", DisplayName = "Log start", ActivityType = "LogMessage", Depth = 1 }],
                WorkflowsSearched = 2
            }
        };
        var tool = new SearchCodebaseTool(ProjectFilesystem(), search);

        var result = await tool.SearchCodebase("/projects/testProcess", "log", "activity");

        Assert.Equal("success", result.Status);
        Assert.IsType<ActivitySearchResult>(result.Data);
    }

    [Fact]
    public async Task SearchCodebase_WorkflowMode_Dispatches() {
        var search = new FakeCodebaseSearchService {
            WorkflowResult = new WorkflowSearchResult {
                Matches = [new WorkflowMatch { FileName = "Main.xaml", FilePath = "/p/Main.xaml", IsMain = true, MatchedOn = "name" }]
            }
        };
        var tool = new SearchCodebaseTool(ProjectFilesystem(), search);

        var result = await tool.SearchCodebase("/projects/testProcess", "main", "workflow");

        Assert.Equal("success", result.Status);
        Assert.IsType<WorkflowSearchResult>(result.Data);
    }

    [Fact]
    public async Task SearchCodebase_ServiceThrows_ReturnsStructuredError() {
        var search = new FakeCodebaseSearchService { ToThrow = new InvalidOperationException("boom") };
        var tool = new SearchCodebaseTool(ProjectFilesystem(), search);

        var result = await tool.SearchCodebase("/projects/testProcess", "queue", "text");

        Assert.Equal("error", result.Status);
        Assert.Contains("boom", result.Errors);
    }
}
