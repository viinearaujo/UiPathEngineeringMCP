using UiPath.Engineering.Mcp.Core.Jobs;
using UiPath.Engineering.Mcp.Core.Models;

namespace UiPath.Engineering.Mcp.Tools.Tests;

public class GetJobToolTests {
    [Fact]
    public void GetJob_WhenInnerResultFailed_ReturnsThatVerdict() {
        var store = new BackgroundJobStore();
        var job = store.Create("validate_project");
        store.Complete(job.JobId, new ToolResult {
            Status = "error",
            Summary = "Validation failed.",
            Errors = ["[validate] boom"],
            Data = new { success = false }
        });

        var result = new GetJobTool(store).GetJob(job.JobId);

        Assert.Equal("error", result.Status);
        Assert.Equal("Validation failed.", result.Summary);
        Assert.Contains("[validate] boom", result.Errors);
        Assert.Equal(BackgroundJobStates.Failed, store.TryGet(job.JobId, out var loaded) ? loaded.State : null);
    }

    [Fact]
    public void GetJob_WhenStillRunning_IsPendingAndDoesNotUseStatusForPhase() {
        var store = new BackgroundJobStore();
        var job = store.Create("validate_project");
        store.MarkRunning(job.JobId);
        store.SetPhase(job.JobId, "Starting uip rpa validate/build/pack.");

        var result = new GetJobTool(store).GetJob(job.JobId);
        var data = System.Text.Json.JsonSerializer.SerializeToElement(result.Data);

        Assert.Equal("pending", result.Status);
        Assert.Equal("Starting uip rpa validate/build/pack.", data.GetProperty("phase").GetString());
        Assert.False(data.TryGetProperty("status", out _));
        Assert.Equal(job.JobId, data.GetProperty("jobId").GetString());
    }

    [Fact]
    public void GetJob_WhenInnerResultSucceeded_ReturnsThatPayload() {
        var store = new BackgroundJobStore();
        var job = store.Create("compile_project");
        store.Complete(job.JobId, new ToolResult {
            Status = "success",
            Summary = "Build completed.",
            Data = new { success = true }
        });

        var result = new GetJobTool(store).GetJob(job.JobId);

        Assert.Equal("success", result.Status);
        Assert.Equal("Build completed.", result.Summary);
    }
}
