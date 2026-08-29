using UiPath.Engineering.Mcp.Core.Models;
using UiPath.Engineering.Mcp.Core.Parsing;

namespace UiPath.Engineering.Mcp.Core.Tests;

public class ProjectAnalysisViewTests {
    private static UiPathProjectModel Sample() {
        var activities = Enumerable.Range(1, 5).Select(i => new ActivityModel {
            Id = $"sequence.1/logmessage.{i}",
            DisplayName = $"Log {i}",
            Type = "LogMessage"
        }).ToList();

        return new UiPathProjectModel {
            ProjectPath = "/projects/perf",
            ProjectName = "Performer",
            MainWorkflow = "Main.xaml",
            EntryPoints = ["Main.xaml"],
            TargetFramework = "Windows",
            Description = "REFramework performer",
            FolderStructure = new DirectoryTreeNode { Name = "perf", Path = "/projects/perf", IsDirectory = true },
            Packages = [new PackageModel { Id = "UiPath.System.Activities", Version = "24.10.0" }],
            Dependencies = ["UiPath.System.Activities"],
            Risks = ["Cycle: A -> B -> A"],
            Workflows = [
                new WorkflowModel {
                    FileName = "Main.xaml", FilePath = "/projects/perf/Main.xaml", IsMain = true,
                    Arguments = [new ArgumentModel { Name = "in_TransactionItem" }],
                    Activities = activities
                },
                new WorkflowModel {
                    FileName = "GetTransactionData.xaml", FilePath = "/projects/perf/GetTransactionData.xaml",
                    Activities = [new ActivityModel { Id = "sequence.1", Type = "Sequence", DisplayName = "Get" }]
                },
                new WorkflowModel {
                    FileName = "Process.xaml", FilePath = "/projects/perf/Process.xaml",
                    HasParseError = true, ParseError = "bad"
                }
            ],
            CodedWorkflows = [
                new CodedWorkflowModel { FileName = "Helpers.cs", ClassName = "Helpers", IsCodedWorkflow = false }
            ],
            Variables = [new VariableModel { Name = "row" }],
            Arguments = [new ArgumentModel { Name = "in_TransactionItem" }],
            InvokeWorkflows = [new InvokeWorkflowModel { SourceWorkflow = "Main.xaml", TargetWorkflow = "Process.xaml" }]
        };
    }

    [Fact]
    public void ToResult_Summary_OmitsActivityTreesAndIncludesCounts() {
        var result = ProjectAnalysisView.ToResult(Sample(), ProjectAnalysisView.DetailSummary, page: 1, pageSize: 20, workflowFile: null);

        Assert.Equal(ProjectAnalysisView.DetailSummary, result.Detail);
        Assert.Null(result.Workflows);
        Assert.Equal(3, result.Summary.Counts.Workflows);
        Assert.Equal(1, result.Summary.Counts.CodedWorkflows);
        Assert.Equal(5, result.Summary.WorkflowIndex[0].ActivityCount);
        Assert.Equal("Main.xaml", result.Summary.WorkflowIndex[0].FileName);
        Assert.True(result.Summary.WorkflowIndex[0].IsMain);
        Assert.True(result.Summary.WorkflowIndex[2].HasParseError);
        Assert.Equal("Helpers", result.Summary.CodedWorkflowIndex[0].ClassName);
        Assert.Equal("UiPath.System.Activities", result.Summary.Packages[0].Id);
        Assert.Contains("Cycle", result.Summary.Risks[0]);
        Assert.False(result.Truncated);
    }

    [Fact]
    public void ToResult_Full_PagesWorkflowsAndKeepsActivities() {
        var result = ProjectAnalysisView.ToResult(Sample(), ProjectAnalysisView.DetailFull, page: 2, pageSize: 1, workflowFile: null);

        Assert.Equal(ProjectAnalysisView.DetailFull, result.Detail);
        Assert.NotNull(result.Workflows);
        Assert.Single(result.Workflows);
        Assert.Equal("GetTransactionData.xaml", result.Workflows[0].FileName);
        Assert.Equal(2, result.Page);
        Assert.Equal(1, result.PageSize);
        Assert.Equal(3, result.TotalWorkflows);
        Assert.True(result.Truncated);
        Assert.Equal(1, result.Workflows[0].Activities.Count);
    }

    [Fact]
    public void ToResult_WorkflowFile_ReturnsThatWorkflowFullEvenOnSummary() {
        var result = ProjectAnalysisView.ToResult(Sample(), ProjectAnalysisView.DetailSummary, page: 1, pageSize: 20, workflowFile: "Main.xaml");

        Assert.NotNull(result.Workflows);
        Assert.Single(result.Workflows);
        Assert.Equal("Main.xaml", result.Workflows[0].FileName);
        Assert.Equal(5, result.Workflows[0].Activities.Count);
        Assert.False(result.Truncated);
    }

    [Fact]
    public void ToResult_UnknownWorkflowFile_ReturnsEmptyWorkflowsAndWarning() {
        var result = ProjectAnalysisView.ToResult(Sample(), ProjectAnalysisView.DetailFull, page: 1, pageSize: 20, workflowFile: "Missing.xaml");

        Assert.NotNull(result.Workflows);
        Assert.Empty(result.Workflows);
        Assert.Contains(result.Warnings, w => w.Contains("Missing.xaml"));
    }

    [Fact]
    public void ToResult_InvalidDetail_ThrowsArgumentException() {
        Assert.Throws<ArgumentException>(() =>
            ProjectAnalysisView.ToResult(Sample(), "tiny", page: 1, pageSize: 20, workflowFile: null));
    }
}
