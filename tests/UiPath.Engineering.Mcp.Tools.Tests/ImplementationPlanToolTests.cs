using System.Text.Json;
using UiPath.Engineering.Mcp.Core.Models;
using UiPath.Engineering.Mcp.Core.Planning;

namespace UiPath.Engineering.Mcp.Tools.Tests;

// The plan tools persist through ImplementationPlanStore on the real filesystem,
// so each class uses a temp directory as the project root while PathGuard checks
// still go through FakeFilesystemProvider.
public class CreateImplementationPlanToolTests : IDisposable {
    private readonly string _projectPath = Path.Combine(Path.GetTempPath(), "mcp-create-plan-" + Guid.NewGuid().ToString("N"));
    private readonly FakeFilesystemProvider _fs;
    private readonly ImplementationPlanStore _store = new();

    public CreateImplementationPlanToolTests() {
        Directory.CreateDirectory(_projectPath);
        _fs = new FakeFilesystemProvider { ProjectJson = Path.Combine(_projectPath, "project.json") };
    }

    public void Dispose() {
        if (Directory.Exists(_projectPath)) {
            Directory.Delete(_projectPath, recursive: true);
        }
    }

    private CreateImplementationPlanTool CreateTool() => new(_fs, _store);

    private static List<PlanTaskInput> TwoTasks() => [
        new PlanTaskInput { Title = "Create Main workflow", TargetFiles = ["Main.xaml"] },
        new PlanTaskInput { Title = "Add logging", Description = "LogMessage at start" }
    ];

    [Fact]
    public void CreateImplementationPlan_WhenPathNotAllowed_ReturnsError() {
        _fs.Allowed = false;

        var result = CreateTool().CreateImplementationPlan(_projectPath, "goal", TwoTasks());

        Assert.Equal("error", result.Status);
        Assert.Null(result.Data);
    }

    [Fact]
    public void CreateImplementationPlan_WhenProjectJsonMissing_ReturnsError() {
        _fs.ProjectJson = null;

        var result = CreateTool().CreateImplementationPlan(_projectPath, "goal", TwoTasks());

        Assert.Equal("error", result.Status);
        Assert.Null(result.Data);
    }

    [Fact]
    public void CreateImplementationPlan_WithoutTasks_ReturnsError() {
        var result = CreateTool().CreateImplementationPlan(_projectPath, "goal", []);

        Assert.Equal("error", result.Status);
        Assert.Null(result.Data);
    }

    [Fact]
    public void CreateImplementationPlan_WithoutGoal_ReturnsError() {
        var result = CreateTool().CreateImplementationPlan(_projectPath, " ", TwoTasks());

        Assert.Equal("error", result.Status);
        Assert.Null(result.Data);
    }

    [Fact]
    public void CreateImplementationPlan_HappyPath_WritesPlanFilesWithSequentialIds() {
        var result = CreateTool().CreateImplementationPlan(_projectPath, "Build it", TwoTasks());
        var data = JsonSerializer.SerializeToElement(result.Data);

        Assert.Equal("success", result.Status);
        Assert.True(File.Exists(ImplementationPlanStore.GetJsonPath(_projectPath)));
        Assert.True(File.Exists(ImplementationPlanStore.GetMarkdownPath(_projectPath)));
        Assert.Equal("Build it", data.GetProperty("Goal").GetString());

        var tasks = data.GetProperty("Tasks");
        Assert.Equal(2, tasks.GetArrayLength());
        Assert.Equal("task-1", tasks[0].GetProperty("Id").GetString());
        Assert.Equal("task-2", tasks[1].GetProperty("Id").GetString());
        Assert.Equal("pending", tasks[0].GetProperty("Status").GetString());
        Assert.Equal("Main.xaml", tasks[0].GetProperty("TargetFiles")[0].GetString());
    }

    [Fact]
    public void CreateImplementationPlan_WhenPlanExists_RefusesUnlessOverwrite() {
        Assert.Equal("success", CreateTool().CreateImplementationPlan(_projectPath, "first", TwoTasks()).Status);

        var refused = CreateTool().CreateImplementationPlan(_projectPath, "second", TwoTasks());
        Assert.Equal("error", refused.Status);
        Assert.Null(refused.Data);
        Assert.Equal("first", _store.Load(_projectPath)!.Goal);

        var overwritten = CreateTool().CreateImplementationPlan(_projectPath, "second", TwoTasks(), overwrite: true);
        Assert.Equal("success", overwritten.Status);
        Assert.Equal("second", _store.Load(_projectPath)!.Goal);
    }
}

public class UpdatePlanTaskToolTests : IDisposable {
    private readonly string _projectPath = Path.Combine(Path.GetTempPath(), "mcp-update-task-" + Guid.NewGuid().ToString("N"));
    private readonly FakeFilesystemProvider _fs;
    private readonly ImplementationPlanStore _store = new();

    public UpdatePlanTaskToolTests() {
        Directory.CreateDirectory(_projectPath);
        _fs = new FakeFilesystemProvider { ProjectJson = Path.Combine(_projectPath, "project.json") };
    }

    public void Dispose() {
        if (Directory.Exists(_projectPath)) {
            Directory.Delete(_projectPath, recursive: true);
        }
    }

    private UpdatePlanTaskTool CreateTool() =>
        new(_fs, _store, new FakeProjectModelBuilder(), DocsSupport.Validator(_fs));

    private void SeedPlan() => _store.Save(_projectPath, new ImplementationPlan {
        Goal = "g",
        Tasks = [new PlanTask { Id = "task-1", Title = "Create Main workflow" }]
    });

    [Fact]
    public async Task UpdatePlanTask_WhenPathNotAllowed_ReturnsError() {
        _fs.Allowed = false;

        var result = await CreateTool().UpdatePlanTask(_projectPath, "task-1", PlanTask.Done);

        Assert.Equal("error", result.Status);
        Assert.Null(result.Data);
    }

    [Fact]
    public async Task UpdatePlanTask_WhenNoPlan_ReturnsError() {
        var result = await CreateTool().UpdatePlanTask(_projectPath, "task-1", PlanTask.Done);

        Assert.Equal("error", result.Status);
        Assert.Contains("No implementation plan", result.Summary);
        Assert.Null(result.Data);
    }

    [Fact]
    public async Task UpdatePlanTask_WhenTaskUnknown_ReturnsError() {
        SeedPlan();

        var result = await CreateTool().UpdatePlanTask(_projectPath, "task-99", PlanTask.Done);

        Assert.Equal("error", result.Status);
        Assert.Contains("task-99", result.Summary);
        Assert.Null(result.Data);
    }

    [Fact]
    public async Task UpdatePlanTask_WhenStatusInvalid_ReturnsError() {
        SeedPlan();

        var result = await CreateTool().UpdatePlanTask(_projectPath, "task-1", "finished");

        Assert.Equal("error", result.Status);
        Assert.Equal(PlanTask.Pending, _store.Load(_projectPath)!.Tasks[0].Status);
    }

    [Fact]
    public async Task UpdatePlanTask_HappyPath_PersistsStatusAndNotes() {
        SeedPlan();

        var result = await CreateTool().UpdatePlanTask(_projectPath, "task-1", PlanTask.InProgress, "started");
        var data = JsonSerializer.SerializeToElement(result.Data);

        Assert.Equal("success", result.Status);
        Assert.Equal("in_progress", data.GetProperty("Status").GetString());
        Assert.Equal("started", data.GetProperty("Notes").GetString());

        var persisted = _store.Load(_projectPath)!.Tasks[0];
        Assert.Equal(PlanTask.InProgress, persisted.Status);
        Assert.Equal("started", persisted.Notes);
    }

    [Fact]
    public async Task UpdatePlanTask_Done_RefusedWhenGeneratedContextIsStale() {
        SeedPlan();
        await CreateTool().UpdatePlanTask(_projectPath, "task-1", PlanTask.InProgress);

        var result = await CreateTool().UpdatePlanTask(_projectPath, "task-1", PlanTask.Done);

        Assert.Equal("error", result.Status);
        Assert.Contains(result.ErrorDetails, e => e.ErrorCode == UiPath.Engineering.Mcp.Core.ToolErrorCodes.DocsStale);
        Assert.Equal(PlanTask.InProgress, _store.Load(_projectPath)!.Tasks[0].Status);
    }

    [Fact]
    public async Task UpdatePlanTask_Done_AllowedAfterSync() {
        SeedPlan();
        DocsSupport.SeedGeneratedContext(_fs, _projectPath);

        var result = await CreateTool().UpdatePlanTask(_projectPath, "task-1", PlanTask.Done);

        Assert.Equal("success", result.Status);
        Assert.Equal(PlanTask.Done, _store.Load(_projectPath)!.Tasks[0].Status);
    }
}

public class GetImplementationPlanToolTests : IDisposable {
    private readonly string _projectPath = Path.Combine(Path.GetTempPath(), "mcp-get-plan-" + Guid.NewGuid().ToString("N"));
    private readonly FakeFilesystemProvider _fs;
    private readonly ImplementationPlanStore _store = new();

    public GetImplementationPlanToolTests() {
        Directory.CreateDirectory(_projectPath);
        _fs = new FakeFilesystemProvider { ProjectJson = Path.Combine(_projectPath, "project.json") };
    }

    public void Dispose() {
        if (Directory.Exists(_projectPath)) {
            Directory.Delete(_projectPath, recursive: true);
        }
    }

    [Fact]
    public void GetImplementationPlan_WhenPathNotAllowed_ReturnsError() {
        _fs.Allowed = false;
        var tool = new GetImplementationPlanTool(_fs, _store);

        var result = tool.GetImplementationPlan(_projectPath);

        Assert.Equal("error", result.Status);
        Assert.Null(result.Data);
    }

    [Fact]
    public void GetImplementationPlan_WhenNoPlan_ReturnsError() {
        var tool = new GetImplementationPlanTool(_fs, _store);

        var result = tool.GetImplementationPlan(_projectPath);

        Assert.Equal("error", result.Status);
        Assert.Contains("No implementation plan", result.Summary);
        Assert.Null(result.Data);
    }

    [Fact]
    public void GetImplementationPlan_HappyPath_ReturnsPlanWithCounts() {
        _store.Save(_projectPath, new ImplementationPlan {
            Goal = "g",
            Tasks = [
                new PlanTask { Id = "task-1", Title = "a", Status = PlanTask.Done },
                new PlanTask { Id = "task-2", Title = "b", Status = PlanTask.InProgress },
                new PlanTask { Id = "task-3", Title = "c" }
            ]
        });
        var tool = new GetImplementationPlanTool(_fs, _store);

        var result = tool.GetImplementationPlan(_projectPath);
        var data = JsonSerializer.SerializeToElement(result.Data);

        Assert.Equal("success", result.Status);
        Assert.Equal("g", data.GetProperty("plan").GetProperty("Goal").GetString());
        var counts = data.GetProperty("counts");
        Assert.Equal(1, counts.GetProperty("pending").GetInt32());
        Assert.Equal(1, counts.GetProperty("inProgress").GetInt32());
        Assert.Equal(1, counts.GetProperty("done").GetInt32());
        Assert.Equal(0, counts.GetProperty("blocked").GetInt32());
    }
}
