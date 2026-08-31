using UiPath.Engineering.Mcp.Core.Models;
using UiPath.Engineering.Mcp.Core.Planning;

namespace UiPath.Engineering.Mcp.Core.Tests;

public class ImplementationPlanStoreTests : IDisposable {
    private readonly string _projectPath = Path.Combine(Path.GetTempPath(), "mcp-plan-" + Guid.NewGuid().ToString("N"));
    private readonly ImplementationPlanStore _store = new();

    public void Dispose() {
        if (Directory.Exists(_projectPath)) {
            Directory.Delete(_projectPath, recursive: true);
        }
    }

    private static ImplementationPlan SamplePlan() => new() {
        Goal = "Build the reconciliation flow",
        CreatedUtc = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero),
        Tasks = [
            new PlanTask {
                Id = "task-1",
                Title = "Create Main workflow",
                Description = "Entry point with TryCatch",
                TargetFiles = ["Main.xaml"],
                AcceptanceCriteria = ["Validation passes"]
            },
            new PlanTask {
                Id = "task-2",
                Title = "Add logging"
            }
        ]
    };

    [Fact]
    public void Load_WhenNoPlanExists_ReturnsNull() {
        Assert.False(_store.Exists(_projectPath));
        Assert.Null(_store.Load(_projectPath));
    }

    [Fact]
    public void Save_WritesJsonAndMarkdownMirror() {
        _store.Save(_projectPath, SamplePlan());

        Assert.True(File.Exists(ImplementationPlanStore.GetJsonPath(_projectPath)));
        Assert.True(File.Exists(ImplementationPlanStore.GetMarkdownPath(_projectPath)));

        var markdown = File.ReadAllText(ImplementationPlanStore.GetMarkdownPath(_projectPath));
        Assert.Contains("# Implementation Plan", markdown);
        Assert.Contains("Build the reconciliation flow", markdown);
        Assert.Contains("task-1: Create Main workflow", markdown);
        Assert.Contains("- Status: pending", markdown);
    }

    [Fact]
    public void Save_ThenLoad_RoundTripsPlan() {
        var plan = SamplePlan();

        _store.Save(_projectPath, plan);
        var loaded = _store.Load(_projectPath);

        Assert.NotNull(loaded);
        Assert.Equal(plan.Goal, loaded.Goal);
        Assert.Equal(plan.CreatedUtc, loaded.CreatedUtc);
        Assert.Equal(2, loaded.Tasks.Count);

        var task = loaded.Tasks[0];
        Assert.Equal("task-1", task.Id);
        Assert.Equal("Create Main workflow", task.Title);
        Assert.Equal("Entry point with TryCatch", task.Description);
        Assert.Equal(PlanTask.Pending, task.Status);
        Assert.Equal(["Main.xaml"], task.TargetFiles);
        Assert.Equal(["Validation passes"], task.AcceptanceCriteria);
        Assert.Null(task.Notes);
    }

    [Fact]
    public void Save_UpdatesTimestampAndPersistsStatusChanges() {
        var plan = SamplePlan();
        _store.Save(_projectPath, plan);

        var loaded = _store.Load(_projectPath)!;
        loaded.Tasks[0].Status = PlanTask.Done;
        loaded.Tasks[0].Notes = "Verified by verify_work.";
        _store.Save(_projectPath, loaded);

        var reloaded = _store.Load(_projectPath)!;
        Assert.Equal(PlanTask.Done, reloaded.Tasks[0].Status);
        Assert.Equal("Verified by verify_work.", reloaded.Tasks[0].Notes);
        Assert.Equal(PlanTask.Pending, reloaded.Tasks[1].Status);
        Assert.True(reloaded.UpdatedUtc >= reloaded.CreatedUtc);
    }

    [Fact]
    public void Save_LeavesNoTempFiles() {
        _store.Save(_projectPath, SamplePlan());

        Assert.False(File.Exists(ImplementationPlanStore.GetJsonPath(_projectPath) + ".tmp"));
        Assert.False(File.Exists(ImplementationPlanStore.GetMarkdownPath(_projectPath) + ".tmp"));
    }

    [Fact]
    public async Task Save_ConcurrentCalls_LeaveValidJson() {
        var tasks = Enumerable.Range(0, 16).Select(i => Task.Run(() => {
            var plan = SamplePlan();
            plan.Goal = "Concurrent goal " + i;
            plan.Tasks[0].Notes = "notes-" + i;
            _store.Save(_projectPath, plan);
        }));

        await Task.WhenAll(tasks);

        var loaded = _store.Load(_projectPath);
        Assert.NotNull(loaded);
        Assert.StartsWith("Concurrent goal ", loaded.Goal);
        Assert.Equal(2, loaded.Tasks.Count);
        Assert.False(File.Exists(ImplementationPlanStore.GetJsonPath(_projectPath) + ".tmp"));
        Assert.False(File.Exists(ImplementationPlanStore.GetMarkdownPath(_projectPath) + ".tmp"));

        var markdown = File.ReadAllText(ImplementationPlanStore.GetMarkdownPath(_projectPath));
        Assert.Contains("# Implementation Plan", markdown);
        Assert.Contains(loaded.Goal, markdown);
    }
}
