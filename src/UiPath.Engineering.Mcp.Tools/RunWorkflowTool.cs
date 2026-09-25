using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using UiPath.Engineering.Mcp.Core;
using UiPath.Engineering.Mcp.Core.Abstractions;
using UiPath.Engineering.Mcp.Core.Jobs;
using UiPath.Engineering.Mcp.Core.Models;
using UiPath.Engineering.Mcp.Core.Parsing;
using UiPath.Engineering.Mcp.Providers.UiPathCli;

namespace UiPath.Engineering.Mcp.Tools;

/// <summary>
/// Runs a workflow through the CLI run verb. Executes arbitrary automation on this machine, so it
/// is gated behind <c>UiPathCli:EnableExecution</c> (fail closed, off by default) and stays off the
/// Copilot default connector.
/// </summary>
[McpServerToolType]
public sealed class RunWorkflowTool {
    private readonly IUiPathCliProvider _cli;
    private readonly IFilesystemProvider _filesystem;
    private readonly CliCommandPolicy _policy;
    private readonly IBackgroundJobStore _jobs;

    public RunWorkflowTool(
        IUiPathCliProvider cli,
        IFilesystemProvider filesystem,
        CliCommandPolicy policy,
        IBackgroundJobStore jobs) {
        _cli = cli;
        _filesystem = filesystem;
        _policy = policy;
        _jobs = jobs;
    }

    [McpServerTool(
        UseStructuredContent = true,
        Title = "Run Workflow",
        ReadOnly = false,
        Destructive = true,
        Idempotent = false),
     Description("Leave-off execution (needs UiPathCli:EnableExecution). Starts uip rpa run as a background job; returns {jobId,status:running}. Poll get_job. Next: get_job.")]
    public Task<ToolResult> RunWorkflow(
        [Description("Absolute path to the UiPath project directory (must contain project.json).")] string projectPath,
        [Description("Workflow or coded file to run, relative to the project root, e.g. 'Main.xaml'. Resolved and verified inside the project before it is passed to the CLI.")] string filePath,
        [Description("Optional repeatable input arguments: 'name=John', 'retries:=3' (raw JSON), or 'payload=@file.json'. Values containing double quotes are rejected — write them to a UTF-8 file and use key=@file.")] List<string>? inputArguments = null,
        [Description("Optional minimum workflow log level to include: Verbose, Trace, Information, Warning, Error, or Critical.")]
        [AllowedValues(
            CliVerbArguments.LogLevelVerbose,
            CliVerbArguments.LogLevelTrace,
            CliVerbArguments.LogLevelInformation,
            CliVerbArguments.LogLevelWarning,
            CliVerbArguments.LogLevelError,
            CliVerbArguments.LogLevelCritical)] string? logLevel = null,
        [Description("Skip the validation and build steps, assuming the project was already built. Use for rapid re-execution when nothing changed.")] bool skipBuild = false,
        [Description("Collect per-activity profiling data and return profilingOutputDirectory (the run's .uistat files and screenshots). Requires a Studio Develop profile with EnableProfiling; needs Studio Desktop.")] bool profiling = false,
        [Description("Profiling delivery mode: endOfRun (default, one summary at completion) or stream (live per-activity entries).")]
        [AllowedValues(CliVerbArguments.ProfilingModeEndOfRun, CliVerbArguments.ProfilingModeStream)] string? profilingMode = null,
        [Description("Include the workflow's log entries in the response (default false). Logs are diagnostic context only — never a verdict.")] bool includeLogEntries = false,
        [Description("Optional CLI timeout in seconds (default 300, max 3600). Raise it for a cold headless Studio restore (30-90s) or a long-running workflow.")] int? timeoutSeconds = null,
        [Description("Optional progress sink. The MCP SDK binds this automatically when the client sent a progress token; do not pass it from a caller.")] IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken cancellationToken = default) {

        var sw = Stopwatch.StartNew();
        if (ToolResults.GuardProject(_filesystem, projectPath, sw) is { } projectFailure) {
            return Task.FromResult(projectFailure);
        }

        if (ResolveTarget(_filesystem, projectPath, filePath, sw, out _, out var relativePath) is { } targetFailure) {
            return Task.FromResult(targetFailure!);
        }

        var arguments = inputArguments ?? [];
        if (CliToolSupport.ValidateInputArguments(arguments, sw) is { } argumentFailure) {
            return Task.FromResult(argumentFailure);
        }

        if (logLevel is not null
            && ToolArgs.ParseChoice(logLevel, "logLevel", CliVerbArguments.RunLogLevels, sw, out _) is { } logLevelFailure) {
            return Task.FromResult(logLevelFailure);
        }

        if (profilingMode is not null
            && ToolArgs.ParseChoice(profilingMode, "profilingMode", CliVerbArguments.ProfilingModes, sw, out _) is { } profilingModeFailure) {
            return Task.FromResult(profilingModeFailure);
        }

        var tokens = CliVerbArguments.WithRpaVerb(CliVerbArguments.Run(
            projectPath, relativePath!, arguments, logLevel, skipBuild, profiling, profilingMode));

        if (CliToolSupport.GuardExecution(_policy, tokens, sw, SuggestedTool) is { } executionFailure) {
            return Task.FromResult(executionFailure);
        }

        _ = cancellationToken;
        var timeout = CliToolSupport.ClampTimeout(timeoutSeconds);
        return Task.FromResult(CliToolSupport.StartBackgroundJob(
            _jobs,
            "run_workflow",
            async (jobProgress, jobCt) => {
                jobProgress.Report($"Running uip rpa run for '{relativePath}'.");
                var outcome = await _cli.RunStructuredAsync(
                    CliVerbArguments.RpaVerb, tokens, projectPath,
                    timeoutSeconds: timeout, jobCt);
                jobProgress.Report($"Run returned; verdict: {(outcome.Verdict?.Succeeded == true ? "success" : "not a clean completion")}.");
                return BuildResult(outcome, relativePath!, profiling, includeLogEntries, Stopwatch.StartNew());
            },
            progress,
            sw));
    }

    internal const string SuggestedTool = "validate_project";

    /// <summary>
    /// Shapes the run verdict. The outer Result and inner HasErrors are the only verdict source.
    /// </summary>
    internal static ToolResult BuildResult(
        UiPathCliRunResult outcome, string filePath, bool profilingRequested, bool includeLogEntries, Stopwatch sw) {
        var cli = outcome.Cli;
        if (outcome.Verdict is not { } verdict) {
            return CliToolSupport.CliFailure(outcome, cli.Summary, sw, SuggestedTool);
        }

        var warnings = new List<string>();
        if (profilingRequested && string.IsNullOrWhiteSpace(verdict.ProfilingOutputDirectory)) {
            warnings.Add("Profiling was requested but no profilingOutputDirectory was returned. Profiling needs a Studio Develop profile with EnableProfiling set, and the run must reach the executor (a compile failure surfaces in errorMessage instead).");
        }

        if (verdict.LogEntriesTruncated) {
            warnings.Add($"logEntries was truncated to the first {verdict.LogEntries.Count} entries. Logs are diagnostic context only, never the verdict.");
        }

        if (!includeLogEntries && verdict.LogEntries.Count > 0) {
            warnings.Add($"{verdict.LogEntries.Count} log entries were omitted; pass includeLogEntries=true to read them (a workflow's Log Message output is the only place its own values appear).");
        }

        var summary = verdict.Succeeded
            ? $"'{filePath}' ran successfully."
            : verdict.IsAwaitingDecision
                ? $"'{filePath}' did not complete: debugState is '{verdict.DebugState}'."
                : $"'{filePath}' failed: {FirstReason(verdict)}";

        var payload = CliToolSupport.VerdictPayload(verdict, cli.Command, cli.ExitCode, includeLogEntries);

        return new ToolResult {
            Status = verdict.Succeeded ? "success" : "error",
            Summary = summary,
            Data = payload,
            Errors = verdict.Succeeded ? [] : FailureEntries(verdict, cli),
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        };
    }

    /// <summary>
    /// Resolves a caller-supplied workflow path to a project-relative path the CLI accepts,
    /// refusing anything outside the project or missing on disk.
    /// </summary>
    internal static ToolResult? ResolveTarget(
        IFilesystemProvider filesystem, string projectPath, string filePath, Stopwatch sw,
        out string? targetPath, out string? relativePath) {
        targetPath = null;
        relativePath = null;

        if (string.IsNullOrWhiteSpace(filePath)) {
            return ToolResults.Failure(new ToolError(
                CliToolErrorCodes.InvalidArgument,
                "filePath is required.",
                "Pass the workflow to run relative to the project root, e.g. 'Main.xaml'."), sw);
        }

        var trimmed = filePath.Trim();
        var candidate = Path.IsPathRooted(trimmed)
            ? trimmed
            : ProjectFilePolicy.CombineProject(projectPath, trimmed);

        if (!PathPolicy.IsWithin(projectPath, candidate, allowEqual: false)) {
            return ToolResults.PathNotAllowed(
                sw,
                "filePath resolves outside the project directory.",
                $"The path '{trimmed}' does not resolve inside '{projectPath}'. Pass it relative to the project root, e.g. 'Main.xaml'.");
        }

        targetPath = candidate;
        relativePath = WorkflowPath.ToRelativePath(projectPath, candidate);

        // Passed relative, not absolute: the CLI falsely rejects an absolute --file-path together
        // with an absolute --project-dir (it normalizes one to forward slashes and string-compares).
        if (!filesystem.FileExists(targetPath)) {
            return ToolResults.Failure(new ToolError(
                ToolErrorCodes.OperationFailed,
                $"The file '{relativePath}' does not exist in the project.",
                "Pass a path relative to the project root, e.g. 'Main.xaml'. Use analyze_project or search_codebase to list workflows."), sw);
        }

        return null;
    }

    private static string FirstReason(CliRunResult verdict) {
        if (!string.IsNullOrWhiteSpace(verdict.ErrorMessage)) {
            return CliToolSupport.Truncate(verdict.ErrorMessage!, 240);
        }

        if (verdict.Errors.Count > 0) {
            var first = verdict.Errors[0];
            var text = string.IsNullOrWhiteSpace(first.ErrorName) ? first.ErrorMessage : $"{first.ErrorName}: {first.ErrorMessage}";
            return CliToolSupport.Truncate(text ?? "execution error", 240);
        }

        if (!string.IsNullOrWhiteSpace(verdict.Output)) {
            return CliToolSupport.Truncate(verdict.Output!, 240);
        }

        return !verdict.EnvelopeSuccess
            ? CliToolSupport.Truncate(verdict.Message ?? $"the CLI returned result '{verdict.Result}'", 240)
            : "the run did not report a clean completion.";
    }

    private static List<string> FailureEntries(CliRunResult verdict, UiPathCliResult cli) {
        var entries = new List<string> { $"[run] {DecisiveSignal(verdict)}: {FirstReason(verdict)}" };

        // A style/analyzer diagnostic lands in errors and can fail the verdict even though the
        // body ran; surface the rest so the caller sees it is not a runtime fault.
        foreach (var error in verdict.Errors.Skip(1).Take(9)) {
            entries.Add($"[run] {error.ErrorName ?? "ERROR"}: {error.ErrorMessage}");
        }

        entries.AddRange(cli.Errors.Where(e => !entries.Contains(e, StringComparer.Ordinal)));
        return entries;
    }

    /// <summary>
    /// Names the field that decided the verdict, so a caller can see it was HasErrors or the flat
    /// errors/output pair — never a log entry's level.
    /// </summary>
    private static string DecisiveSignal(CliRunResult verdict) {
        if (!verdict.EnvelopeSuccess) {
            return $"result={verdict.Result}";
        }

        if (verdict.DebugState is not null && verdict.IsAwaitingDecision) {
            return $"debugState={verdict.DebugState}";
        }

        if (verdict.HasErrors is true) {
            return "hasErrors=true";
        }

        return verdict.Errors.Count > 0 ? "errors" : "output";
    }
}
