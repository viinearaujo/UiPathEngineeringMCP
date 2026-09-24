using System.Text.Json;
using UiPath.Engineering.Mcp.Core.GapAnalysis;
using UiPath.Engineering.Mcp.Core.Models;
using UiPath.Engineering.Mcp.Core.Planning;

namespace UiPath.Engineering.Mcp.Tools.Tests;

public class AnalyzeProjectGapsToolTests : IDisposable {
    private readonly string _projectPath = Path.Combine(Path.GetTempPath(), "mcp-gaps-tool-" + Guid.NewGuid().ToString("N"));
    private readonly FakeFilesystemProvider _fs;
    private readonly ImplementationPlanStore _store;

    public AnalyzeProjectGapsToolTests() {
        Directory.CreateDirectory(_projectPath);
        _fs = new FakeFilesystemProvider { ProjectJson = Path.Combine(_projectPath, "project.json") };
        _store = new ImplementationPlanStore(_fs);
    }

    public void Dispose() {
        if (Directory.Exists(_projectPath)) {
            Directory.Delete(_projectPath, recursive: true);
        }
    }

    private AnalyzeProjectGapsTool CreateTool(FakeProjectModelBuilder modelBuilder) =>
        new(_fs, modelBuilder, _store, DocsSupport.Validator(_fs));

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
    public async Task AnalyzeProjectGaps_WhenModelBuilderThrows_PropagatesToHostExceptionBoundary() {
        var tool = CreateTool(new FakeProjectModelBuilder { ToThrow = new FileNotFoundException("project.json not found in the specified directory.") });

        await Assert.ThrowsAsync<FileNotFoundException>(() => tool.AnalyzeProjectGaps(_projectPath));
    }

    [Fact]
    public async Task AnalyzeProjectGaps_CleanProjectWithoutPlan_ReportsZeroGapsAndNoPlan() {
        var model = CleanModel(_projectPath);
        DocsSupport.SeedGeneratedContext(_fs, _projectPath, model);
        var tool = CreateTool(new FakeProjectModelBuilder { Model = model });

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
        DocsSupport.SeedGeneratedContext(_fs, _projectPath, model);
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

    [Fact]
    public async Task AnalyzeProjectGaps_ReportsConfidenceAndCategoryBreakdowns() {
        var model = CleanModel(_projectPath);
        // Undocumented orphan: a medium-confidence structure gap. The bare LogMessage body
        // below adds a low-confidence readability hint, so both buckets are exercised.
        model.Workflows.Add(new WorkflowModel {
            FileName = "Unused.xaml",
            Description = "Orphan.",
            Activities = [new ActivityModel { Id = "sequence.1", Type = "Sequence", DisplayName = "Sequence" }]
        });
        DocsSupport.SeedGeneratedContext(_fs, _projectPath, model);
        var tool = CreateTool(new FakeProjectModelBuilder { Model = model });

        var result = await tool.AnalyzeProjectGaps(_projectPath);
        var data = JsonSerializer.SerializeToElement(result.Data);

        var confidence = data.GetProperty("confidence");
        Assert.Equal(1, confidence.GetProperty("medium").GetInt32());
        Assert.Equal(1, confidence.GetProperty("low").GetInt32());

        var categories = data.GetProperty("categories").EnumerateArray()
            .ToDictionary(c => c.GetProperty("category").GetString()!, c => c.GetProperty("count").GetInt32());
        Assert.Equal(1, categories["structure"]);
        Assert.Equal(1, categories[Gap.CategoryReadability]);
        // Reporting order puts blocking categories ahead of advisory ones.
        var ordered = data.GetProperty("categories").EnumerateArray()
            .Select(c => c.GetProperty("category").GetString()).ToList();
        Assert.Equal(["structure", Gap.CategoryReadability], ordered);

        // Every gap carries a confidence so the caller can tell a fact from a hint.
        Assert.All(data.GetProperty("gaps").EnumerateArray(), g =>
            Assert.Contains(g.GetProperty("Confidence").GetString(),
                [Gap.ConfidenceHigh, Gap.ConfidenceMedium, Gap.ConfidenceLow]));
    }

    [Fact]
    public async Task AnalyzeProjectGaps_KeepsCountsAndPlanShapeIntact() {
        var model = CleanModel(_projectPath);
        DocsSupport.SeedGeneratedContext(_fs, _projectPath, model);
        var tool = CreateTool(new FakeProjectModelBuilder { Model = model });

        var result = await tool.AnalyzeProjectGaps(_projectPath);
        var data = JsonSerializer.SerializeToElement(result.Data);

        Assert.Equal(
            ["gaps", "counts", "confidence", "categories", "plan"],
            data.EnumerateObject().Select(p => p.Name).ToList());
        Assert.Equal(
            ["error", "warning", "info"],
            data.GetProperty("counts").EnumerateObject().Select(p => p.Name).ToList());
        Assert.Equal(
            ["exists", "tasksDone", "tasksTotal"],
            data.GetProperty("plan").EnumerateObject().Select(p => p.Name).ToList());
    }

    [Fact]
    public async Task AnalyzeProjectGaps_PlanCrossCheckUsesFilesystemProvider_NotRawDisk() {
        // The plan target exists only in the provider's in-memory view; nothing was written
        // to disk. The analyzer must see it, which proves the seam is used.
        _store.Save(_projectPath, new ImplementationPlan {
            Goal = "g",
            Tasks = [new PlanTask { Id = "task-1", Title = "Create Main", TargetFiles = ["Main.xaml"] }]
        });
        var model = CleanModel(_projectPath);
        _fs.FileContents[Path.Combine(_projectPath, "Main.xaml")] = "<Activity />";
        DocsSupport.SeedGeneratedContext(_fs, _projectPath, model);
        var tool = CreateTool(new FakeProjectModelBuilder { Model = model });

        var result = await tool.AnalyzeProjectGaps(_projectPath);
        var data = JsonSerializer.SerializeToElement(result.Data);

        Assert.Contains(data.GetProperty("gaps").EnumerateArray(),
            g => g.GetProperty("Id").GetString() == "plan-task-possibly-complete:task-1");
    }
}
