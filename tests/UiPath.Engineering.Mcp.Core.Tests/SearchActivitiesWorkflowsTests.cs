using UiPath.Engineering.Mcp.Core.CodeAnalysis;
using UiPath.Engineering.Mcp.Core.CodeSearch;
using UiPath.Engineering.Mcp.Core.Models;
using UiPath.Engineering.Mcp.Core.Parsing;

namespace UiPath.Engineering.Mcp.Core.Tests;

public class SearchActivitiesWorkflowsTests {
    private const string Root = "/projects/testProcess";

    private sealed class StubContextBuilder : ICSharpContextBuilder {
        public Task<CSharpAnalysisContext> BuildAsync(string projectPath, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeProjectModelBuilder(UiPathProjectModel model) : IProjectModelBuilder {
        public Task<UiPathProjectModel> BuildAsync(string projectPath, CancellationToken cancellationToken = default) =>
            Task.FromResult(model);
    }

    private static CodebaseSearchService CreateService(UiPathProjectModel model) =>
        new(new StubContextBuilder(), new FakeProjectModelBuilder(model), new FakeFilesystemProvider());

    private static UiPathProjectModel BuildModel() => new() {
        ProjectName = "testProcess",
        Workflows = [
            new WorkflowModel {
                FileName = "Main.xaml",
                FilePath = "/projects/testProcess/Main.xaml",
                IsMain = true,
                Description = "Entry point for invoice processing",
                Activities = [
                    new ActivityModel { Id = "sequence.1/logmessage.1", DisplayName = "Log start", Type = "LogMessage", Depth = 1, Line = 12 },
                    new ActivityModel { Id = "sequence.1/logmessage.2", DisplayName = "Log", Type = "LogMessage", Depth = 1, Line = 13 },
                    new ActivityModel { Id = "sequence.1/writeline.3", DisplayName = "Write line", Type = "WriteLine", Depth = 2, Line = 14 }
                ]
            },
            new WorkflowModel {
                FileName = "InvoiceFlow.xaml",
                FilePath = "/projects/testProcess/InvoiceFlow.xaml",
                Activities = [
                    new ActivityModel { DisplayName = "Log invoice", Type = "LogMessage", Depth = 1 }
                ]
            },
            new WorkflowModel {
                FileName = "Broken.xaml",
                FilePath = "/projects/testProcess/Broken.xaml",
                HasParseError = true,
                ParseError = "XAML parse failure: boom"
            }
        ]
    };

    // --- activity mode ---

    [Fact]
    public async Task SearchActivities_MatchesDisplayNameAndTypeAcrossWorkflows() {
        var sut = CreateService(BuildModel());

        var result = await sut.SearchActivitiesAsync(Root, "log");

        Assert.Equal(3, result.Matches.Count);
        Assert.All(result.Matches, m => Assert.Equal("LogMessage", m.ActivityType));
        Assert.Contains(result.Matches, m => m.WorkflowFile == "InvoiceFlow.xaml" && m.DisplayName == "Log invoice");
        Assert.Equal(2, result.WorkflowsSearched); // Broken.xaml skipped
        Assert.Contains("1 workflow(s) failed to parse", result.Note);
        Assert.Contains("per-parse-snapshot", result.Note);
        var logStart = result.Matches.Single(m => m.DisplayName == "Log start");
        Assert.Equal("sequence.1/logmessage.1", logStart.Id);
        Assert.Equal(12, logStart.Line);
    }

    [Fact]
    public async Task SearchActivities_ExactNameMatch_OrdersBeforeCaseInsensitiveOnly() {
        var sut = CreateService(BuildModel());

        var result = await sut.SearchActivitiesAsync(Root, "Log");

        Assert.Equal(3, result.Matches.Count);
        Assert.Equal("Log", result.Matches[0].DisplayName); // exact ordinal-name equality
    }

    [Fact]
    public async Task SearchActivities_TypeOnlyMatch_PassesDepthThrough() {
        var sut = CreateService(BuildModel());

        var result = await sut.SearchActivitiesAsync(Root, "WriteLine");

        var match = Assert.Single(result.Matches);
        Assert.Equal("Write line", match.DisplayName);
        Assert.Equal(2, match.Depth);
        Assert.Equal("/projects/testProcess/Main.xaml", match.WorkflowPath);
    }

    // --- workflow mode ---

    [Fact]
    public async Task SearchWorkflows_MatchesFileNameAndDescription() {
        var sut = CreateService(BuildModel());

        var result = await sut.SearchWorkflowsAsync(Root, "invoice");

        Assert.Equal(2, result.Matches.Count);
        var byName = Assert.Single(result.Matches, m => m.FileName == "InvoiceFlow.xaml");
        Assert.Equal("name", byName.MatchedOn);
        var byDescription = Assert.Single(result.Matches, m => m.FileName == "Main.xaml");
        Assert.Equal("description", byDescription.MatchedOn);
        Assert.True(byDescription.IsMain);
    }

    [Fact]
    public async Task SearchWorkflows_NameAndDescriptionHit_MatchedOnBoth() {
        var model = new UiPathProjectModel {
            ProjectName = "testProcess",
            Workflows = [
                new WorkflowModel {
                    FileName = "Invoice.xaml",
                    FilePath = "/projects/testProcess/Invoice.xaml",
                    Description = "Handles invoice retries"
                }
            ]
        };
        var sut = CreateService(model);

        var result = await sut.SearchWorkflowsAsync(Root, "invoice");

        var match = Assert.Single(result.Matches);
        Assert.Equal("both", match.MatchedOn);
    }

    [Fact]
    public async Task SearchWorkflows_ParseErrorWorkflow_StillNameMatchableWithNote() {
        var sut = CreateService(BuildModel());

        var result = await sut.SearchWorkflowsAsync(Root, "broken");

        var match = Assert.Single(result.Matches);
        Assert.Equal("Broken.xaml", match.FileName);
        Assert.Equal("name", match.MatchedOn);
        Assert.Contains("1 workflow(s) failed to parse", result.Note);
    }

    [Fact]
    public async Task SearchWorkflows_ExactNameMatch_OrdersFirst() {
        var model = new UiPathProjectModel {
            ProjectName = "testProcess",
            Workflows = [
                new WorkflowModel { FileName = "ALogin.xaml", FilePath = "/projects/testProcess/ALogin.xaml" },
                new WorkflowModel { FileName = "Log.xaml", FilePath = "/projects/testProcess/Log.xaml" }
            ]
        };
        var sut = CreateService(model);

        var result = await sut.SearchWorkflowsAsync(Root, "Log");

        Assert.Equal(2, result.Matches.Count);
        Assert.Equal("Log.xaml", result.Matches[0].FileName); // exact ordinal-name equality
    }

    [Fact]
    public async Task SearchWorkflows_ExactFullFileNameMatch_OrdersFirst() {
        var model = new UiPathProjectModel {
            ProjectName = "testProcess",
            Workflows = [
                new WorkflowModel { FileName = "ALogin.xaml", FilePath = "/projects/testProcess/ALogin.xaml" },
                new WorkflowModel { FileName = "Log.xaml", FilePath = "/projects/testProcess/Log.xaml" }
            ]
        };
        var sut = CreateService(model);

        var result = await sut.SearchWorkflowsAsync(Root, "Log.xaml");

        Assert.Equal("Log.xaml", result.Matches[0].FileName); // exact tier fires for the full-filename form too
    }
}
