using System.Text.Json;
using UiPath.Engineering.Mcp.Core.Models;
using UiPath.Engineering.Mcp.Core.Planning;

namespace UiPath.Engineering.Mcp.Tools.Tests;

public class AnalyzeProjectGapsToolTests : IDisposable {
    private readonly string _projectPath = Path.Combine(Path.GetTempPath(), "mcp-gaps-tool-" + Guid.NewGuid().ToString("N"));
    private readonly FakeFilesystemProvider _fs;
    private readonly ImplementationPlanStore _store = new();

    public AnalyzeProjectGapsToolTests() {
        Directory.CreateDirectory(_projectPath);
        _fs = new FakeFilesystemProvider { ProjectJson = Path.Combine(_projectPath, "project.json") };
    }

    public void Dispose() {
        if (Directory.Exists(_projectPath)) {
            Directory.Delete(_projectPath, recursive: true);
        }
    }

    private AnalyzeProjectGapsTool CreateTool(FakeProjectModelBuilder modelBuilder) =>
        new(_fs, modelBuilder, _store);

    private static UiPathProjectModel CleanModel(string projectPath) => new() {
        ProjectPath = projectPath,
        ProjectName = "clean",
        MainWorkflow = "Main.xaml",
        Workflows = [
            new WorkflowModel {
                FileName = "Main.xaml",
                IsMain = true,
                Description = "Entry point.",
                ExceptionHandlers = [new ExceptionHandlerModel { WorkflowName = "Main.xaml" }],
                LogMessages = [new LogMessageModel()],
                InvokeWorkflows = [new InvokeWorkflowModel { SourceWorkflow = "Main.xaml", TargetWorkflow = "Child.xaml" }]
            },
            new WorkflowModel { FileName = "Child.xaml", Description = "Child." },
            new WorkflowModel { FileName = "Tests/TestMain.xaml", Description = "Tests." }
        ]
    };

    [Fact]
    public async Task AnalyzeProjectGaps_WhenPathNotAllowed_ReturnsError() {
        _fs.Allowed = false;
        var tool = CreateTool(new FakeProjectModelBuilder());

        var result = await tool.AnalyzeProjectGaps(_projectPath);

        Assert.Equal("error", result.Status);
        Assert.Equal("Path not allowed.", result.Summary);
        Assert.Null(result.Data);
    }

    [Fact]
    public async Task AnalyzeProjectGaps_WhenProjectJsonMissing_ReturnsError() {
        _fs.ProjectJson = null;
        var tool = CreateTool(new FakeProjectModelBuilder());

        var result = await tool.AnalyzeProjectGaps(_projectPath);

        Assert.Equal("error", result.Status);
        Assert.Equal("project.json not found.", result.Summary);
        Assert.Null(result.Data);
    }

    [Fact]
    public async Task AnalyzeProjectGaps_WhenModelBuilderThrows_ReturnsStructuredError() {
        var tool = CreateTool(new FakeProjectModelBuilder { ToThrow = new FileNotFoundException("project.json not found in the specified directory.") });

        var result = await tool.AnalyzeProjectGaps(_projectPath);

        Assert.Equal("error", result.Status);
        Assert.Equal("project.json not found.", result.Summary);
        Assert.Null(result.Data);
    }

    [Fact]
    public async Task AnalyzeProjectGaps_CleanProjectWithoutPlan_ReportsZeroGapsAndNoPlan() {
        var tool = CreateTool(new FakeProjectModelBuilder { Model = CleanModel(_projectPath) });

        var result = await tool.AnalyzeProjectGaps(_projectPath);
        var data = JsonSerializer.SerializeToElement(result.Data);

        Assert.Equal("success", result.Status);
        Assert.Equal(0, data.GetProperty("gaps").GetArrayLength());
        Assert.Equal(0, data.GetProperty("counts").GetProperty("error").GetInt32());
        Assert.False(data.GetProperty("plan").GetProperty("exists").GetBoolean());
        Assert.Equal(0, data.GetProperty("plan").GetProperty("tasksTotal").GetInt32());
    }

    [Fact]
    public async Task AnalyzeProjectGaps_WithGapsAndPlan_ReportsCountsAndPlanProgress() {
        _store.Save(_projectPath, new ImplementationPlan {
            Goal = "g",
            Tasks = [
                new PlanTask { Id = "task-1", Title = "a", Status = PlanTask.Done },
                new PlanTask { Id = "task-2", Title = "b" }
            ]
        });
        var model = CleanModel(_projectPath);
        model.Workflows.Add(new WorkflowModel { FileName = "Unused.xaml", Description = "Orphan." });
        var tool = CreateTool(new FakeProjectModelBuilder { Model = model });

        var result = await tool.AnalyzeProjectGaps(_projectPath);
        var data = JsonSerializer.SerializeToElement(result.Data);

        Assert.Equal("success", result.Status);
        Assert.Equal(1, data.GetProperty("counts").GetProperty("warning").GetInt32());
        Assert.Contains(data.GetProperty("gaps").EnumerateArray(),
            g => g.GetProperty("Id").GetString() == "orphan-workflow:Unused.xaml");

        var plan = data.GetProperty("plan");
        Assert.True(plan.GetProperty("exists").GetBoolean());
        Assert.Equal(1, plan.GetProperty("tasksDone").GetInt32());
        Assert.Equal(2, plan.GetProperty("tasksTotal").GetInt32());
    }
}
