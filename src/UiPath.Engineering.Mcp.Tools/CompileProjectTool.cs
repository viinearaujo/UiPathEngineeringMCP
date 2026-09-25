using System.ComponentModel;
using System.Diagnostics;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using UiPath.Engineering.Mcp.Core.Abstractions;
using UiPath.Engineering.Mcp.Core.Jobs;
using UiPath.Engineering.Mcp.Core.Models;
using UiPath.Engineering.Mcp.Providers.UiPathCli;

namespace UiPath.Engineering.Mcp.Tools;

[McpServerToolType]
public sealed class CompileProjectTool {
    private readonly IUiPathCliProvider _cliProvider;
    private readonly IFilesystemProvider _filesystem;
    private readonly IBackgroundJobStore _jobs;

    public CompileProjectTool(
        IUiPathCliProvider cliProvider,
        IFilesystemProvider filesystem,
        IBackgroundJobStore jobs) {
        _cliProvider = cliProvider;
        _filesystem = filesystem;
        _jobs = jobs;
    }

    [McpServerTool(
        UseStructuredContent = true,
        Title = "Compile Project",
        ReadOnly = false,
        Destructive = true,
        Idempotent = false),
     Description("Leave-off CLI build as a background job; returns {jobId,status:running}. Prefer validate_project(build:true) or check_work. Next: get_job.")]
    public Task<ToolResult> CompileProject(
        [Description("Absolute path to the UiPath project directory.")] string projectPath,
        [Description("Optional progress sink. The MCP SDK binds this automatically when the client sent a progress token; do not pass it from a caller.")] IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken cancellationToken = default) {
        var sw = Stopwatch.StartNew();
        if (ToolResults.GuardProject(_filesystem, projectPath, sw) is { } guardFailure) {
            return Task.FromResult(guardFailure);
        }

        _ = cancellationToken;
        return Task.FromResult(CliToolSupport.StartBackgroundJob(
            _jobs,
            "compile_project",
            async (jobProgress, jobCt) => {
                jobProgress.Report("Running uip rpa build.");
                var cliResult = await _cliProvider.ValidateAsync(projectPath, validate: false, build: true, pack: false, jobCt);
                jobProgress.Report(cliResult.Success ? "build succeeded." : "build reported errors.");
                return new ToolResult {
                    Status = cliResult.Success ? "success" : "error",
                    Summary = cliResult.Summary,
                    Data = new {
                        success = cliResult.Success,
                        build = new {
                            executed = cliResult.Build.Executed,
                            success = cliResult.Build.Executed && cliResult.Build.Success,
                            errors = cliResult.Build.Errors,
                            warnings = cliResult.Build.Warnings
                        },
                        errors = cliResult.Errors,
                        warnings = cliResult.Warnings
                    },
                    Errors = cliResult.Errors,
                    Warnings = cliResult.Warnings,
                    DurationMs = sw.ElapsedMilliseconds
                };
            },
            progress,
            sw));
    }
}
