using System.ComponentModel;
using System.Diagnostics;
using ModelContextProtocol.Server;
using UiPath.Engineering.Mcp.Core.Jobs;
using UiPath.Engineering.Mcp.Core.Models;

namespace UiPath.Engineering.Mcp.Tools;

[McpServerToolType]
public sealed class GetJobTool {
    private readonly IBackgroundJobStore _jobs;

    public GetJobTool(IBackgroundJobStore jobs) => _jobs = jobs;

    [McpServerTool(
        UseStructuredContent = true,
        Title = "Get Job",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true),
     Description("Polls a background CLI job by jobId. Returns status/phase; when finished, the same ToolResult payload the tool would have returned. Cancelling this poll does not kill the job. Next: update_plan_task or fix errors.")]
    public ToolResult GetJob(
        [Description("Job id returned by validate_project or another async CLI tool.")] string jobId) {
        var sw = Stopwatch.StartNew();
        if (string.IsNullOrWhiteSpace(jobId)) {
            return ToolResults.Failure("jobId is required.", sw);
        }

        if (!_jobs.TryGet(jobId.Trim(), out var job)) {
            return ToolResults.Failure($"Job '{jobId}' was not found (expired or unknown).", sw);
        }

        var finished = job.State is BackgroundJobStates.Succeeded or BackgroundJobStates.Failed;
        return ToolResults.Ok(
            finished
                ? $"Job '{job.JobId}' {job.State}."
                : $"Job '{job.JobId}' is {job.State}. Poll again.",
            new {
                jobId = job.JobId,
                tool = job.ToolName,
                status = job.State,
                phase = job.Phase,
                result = job.Result,
                error = job.Error,
                createdUtc = job.CreatedUtc,
                finishedUtc = job.FinishedUtc
            },
            sw);
    }
}
