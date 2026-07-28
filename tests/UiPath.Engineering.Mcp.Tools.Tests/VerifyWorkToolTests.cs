using System.Text.Json;
using UiPath.Engineering.Mcp.Core.Models;
using UiPath.Engineering.Mcp.Core.Planning;
using UiPath.Engineering.Mcp.Providers.UiPathCli;

namespace UiPath.Engineering.Mcp.Tools.Tests;

public class VerifyWorkToolTests : IDisposable {
    private readonly string _projectPath = Path.Combine(Path.GetTempPath(), "mcp-verify-" + Guid.NewGuid().ToString("N"));
    private readonly FakeFilesystemProvider _fs;
    private readonly FakeUiPathCliProvider _cli = new();
    private readonly ImplementationPlanStore _store = new();

    public VerifyWorkToolTests() {
        Directory.CreateDirectory(_projectPath);
        _fs = new FakeFilesystemProvider { ProjectJson = Path.Combine(_projectPath, "project.json") };
    }

    public void Dispose() {
        if (Directory.Exists(_projectPath)) {
            Directory.Delete(_projectPath, recursive: true);
        }
    }

    private VerifyWorkTool CreateTool() => new(_fs, new FakeProjectModelBuilder(), _cli, _store);

    // Seeds a plan whose task-1 expects Main.xaml, and registers Main.xaml as
    // existing for the filesystem fake (resolved the same way the tool resolves it).
    private void SeedPlanWithExistingTarget() {
        _store.Save(_projectPath, new ImplementationPlan {
            Goal = "g",
            Tasks = [new PlanTask { Id = "task-1", Title = "Create Main workflow", TargetFiles = ["Main.xaml"] }]
        });
        _fs.ExistingFiles.Add(Path.Combine(Path.GetFullPath(_projectPath), "Main.xaml"));
    }

    [Fact]
    public async Task VerifyWork_WhenPathNotAllowed_ReturnsError() {
        _fs.Allowed = false;

        var result = await CreateTool().VerifyWork(_projectPath, ["task-1"]);

        Assert.Equal("error", result.Status);
        Assert.Equal("Path not allowed.", result.Summary);
        Assert.Null(result.Data);
    }

    [Fact]
    public async Task VerifyWork_WhenNoPlanAndTaskIdsGiven_ReturnsError() {
        var result = await CreateTool().VerifyWork(_projectPath, ["task-1"]);

        Assert.Equal("error", result.Status);
        Assert.Contains("No implementation plan", result.Summary);
        Assert.Null(result.Data);
    }

    [Fact]
    public async Task VerifyWork_WhenTaskUnknown_ReturnsError() {
        SeedPlanWithExistingTarget();

        var result = await CreateTool().VerifyWork(_projectPath, ["task-99"]);

        Assert.Equal("error", result.Status);
        Assert.Contains("task-99", result.Summary);
        Assert.Null(result.Data);
    }

    [Fact]
    public async Task VerifyWork_CliSuccessAndExpectationsMet_MarksTasksDone() {
        SeedPlanWithExistingTarget();
        _cli.Result = new UiPathCliResult { Success = true, Summary = "Validation completed." };

        var result = await CreateTool().VerifyWork(_projectPath, ["task-1"]);
        var data = JsonSerializer.SerializeToElement(result.Data);

        Assert.Equal("success", result.Status);
        Assert.Equal((true, true, false), _cli.LastValidateFlags);
        Assert.True(data.GetProperty("validation").GetProperty("success").GetBoolean());
        var updated = Assert.Single(data.GetProperty("tasksUpdated").EnumerateArray());
        Assert.Equal("task-1", updated.GetProperty("taskId").GetString());
        Assert.Equal("done", updated.GetProperty("status").GetString());
        Assert.Equal(1, data.GetProperty("expectations").GetProperty("checked").GetInt32());
        Assert.Equal(0, data.GetProperty("expectations").GetProperty("missing").GetArrayLength());

        Assert.Equal(PlanTask.Done, _store.Load(_projectPath)!.Tasks[0].Status);
    }

    [Fact]
    public async Task VerifyWork_CliFailure_MarksTasksBlockedWithErrors() {
        SeedPlanWithExistingTarget();
        _cli.Result = new UiPathCliResult {
            Success = false,
            Summary = "Validation failed.",
            Errors = ["[validate] boom"]
        };

        var result = await CreateTool().VerifyWork(_projectPath, ["task-1"]);
        var data = JsonSerializer.SerializeToElement(result.Data);

        Assert.Equal("error", result.Status);
        Assert.Contains("[validate] boom", result.Errors);
        var updated = Assert.Single(data.GetProperty("tasksUpdated").EnumerateArray());
        Assert.Equal("blocked", updated.GetProperty("status").GetString());

        var persisted = _store.Load(_projectPath)!.Tasks[0];
        Assert.Equal(PlanTask.Blocked, persisted.Status);
        Assert.Contains("[validate] boom", persisted.Notes);
    }

    [Fact]
    public async Task VerifyWork_CliThrows_ReportsErrorAndLeavesTasksUnchanged() {
        SeedPlanWithExistingTarget();
        _cli.ValidateException = new InvalidOperationException("uip.exe not found");

        var result = await CreateTool().VerifyWork(_projectPath, ["task-1"]);

        Assert.Equal("error", result.Status);
        Assert.Contains("uip.exe not found", result.Errors);
        Assert.Null(result.Data);
        Assert.Equal(PlanTask.Pending, _store.Load(_projectPath)!.Tasks[0].Status);
    }

    [Fact]
    public async Task VerifyWork_CliSuccessButExpectedFileMissing_LeavesTasksUnchanged() {
        _store.Save(_projectPath, new ImplementationPlan {
            Goal = "g",
            Tasks = [new PlanTask { Id = "task-1", Title = "Create Main workflow", TargetFiles = ["Main.xaml"] }]
        });
        _cli.Result = new UiPathCliResult { Success = true, Summary = "Validation completed." };

        var result = await CreateTool().VerifyWork(_projectPath, ["task-1"]);
        var data = JsonSerializer.SerializeToElement(result.Data);

        Assert.Equal("error", result.Status);
        Assert.Contains("Expected file missing: Main.xaml", result.Errors);
        Assert.Empty(data.GetProperty("tasksUpdated").EnumerateArray());
        Assert.Equal("Main.xaml", data.GetProperty("expectations").GetProperty("missing")[0].GetString());
        Assert.Equal(PlanTask.Pending, _store.Load(_projectPath)!.Tasks[0].Status);
    }

    [Fact]
    public async Task VerifyWork_WithoutTaskIds_OnlyChecksExpectedFiles() {
        _fs.ExistingFiles.Add(Path.Combine(Path.GetFullPath(_projectPath), "Out", "report.xaml"));
        _cli.Result = new UiPathCliResult { Success = true, Summary = "Validation completed." };

        var result = await CreateTool().VerifyWork(_projectPath, expectedFiles: ["Out/report.xaml"]);
        var data = JsonSerializer.SerializeToElement(result.Data);

        Assert.Equal("success", result.Status);
        Assert.Equal(1, data.GetProperty("expectations").GetProperty("checked").GetInt32());
        Assert.Equal(0, data.GetProperty("tasksUpdated").GetArrayLength());
    }
}
