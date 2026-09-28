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
     Description("Polls a background CLI job by jobId. While it is running, status is pending and data.phase is the latest phase. When finished, returns the same ToolResult the tool would have returned, so status is that verdict. Cancelling this poll does not kill the job. Next: update_plan_task or fix errors.")]
    public ToolResult GetJob(
        [Description("Job id returned by validate_project or another async CLI tool.")] string jobId) {
        var sw = Stopwatch.StartNew();
        if (string.IsNullOrWhiteSpace(jobId)) {
            return ToolResults.Failure("jobId is required.", sw);
        }

        if (!_jobs.TryGet(jobId.Trim(), out var job)) {
            return ToolResults.Failure($"Job '{jobId}' was not found (expired or unknown).", sw);
        }

        if (job.State is BackgroundJobStates.Succeeded or BackgroundJobStates.Failed) {
            if (job.Result is ToolResult finished) {
                return finished;
            }

            if (job.State == BackgroundJobStates.Failed) {
                return new ToolResult {
                    Status = "error",
                    Summary = "The background job failed.",
                    Errors = string.IsNullOrWhiteSpace(job.Error) ? [] : [job.Error],
                    Data = new {
                        jobId = job.JobId,
                        tool = job.ToolName,
                        phase = job.Phase ?? job.State
                    },
                    DurationMs = sw.ElapsedMilliseconds
                };
            }
        }

        return new ToolResult {
            Status = "pending",
            Summary = $"Job '{job.JobId}' is {job.State}. Poll again.",
            Data = new {
                jobId = job.JobId,
                tool = job.ToolName,
                phase = job.Phase ?? job.State,
                createdUtc = job.CreatedUtc
            },
            DurationMs = sw.ElapsedMilliseconds
        };
    }
}
