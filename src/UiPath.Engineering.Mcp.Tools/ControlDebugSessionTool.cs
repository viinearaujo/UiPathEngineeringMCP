using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using ModelContextProtocol.Server;
using UiPath.Engineering.Mcp.Core.Abstractions;
using UiPath.Engineering.Mcp.Core.Models;
using UiPath.Engineering.Mcp.Providers.UiPathCli;

namespace UiPath.Engineering.Mcp.Tools;

/// <summary>
/// Drives one debug session through the CLI debug verbs: start, read DebugState/DebugDetails,
/// step, continue past an exception, set breakpoints, and cancel. Executes arbitrary automation,
/// so every command is gated behind <c>UiPathCli:EnableExecution</c> (fail closed) and the tool
/// stays off the Copilot default connector.
/// </summary>
[McpServerToolType]
public sealed class ControlDebugSessionTool {
    private readonly IUiPathCliProvider _cli;
    private readonly IFilesystemProvider _filesystem;
    private readonly CliCommandPolicy _policy;

    public ControlDebugSessionTool(IUiPathCliProvider cli, IFilesystemProvider filesystem, CliCommandPolicy policy) {
        _cli = cli;
        _filesystem = filesystem;
        _policy = policy;
    }

    [McpServerTool(UseStructuredContent = true), Description("Drives ONE UiPath debug session (uip rpa debug start|state|step-*|continue*|resume|break|restart-from-top|set-breakpoints, plus execution cancel) and returns DebugState, DebugDetails, and the verdict. EXECUTES ARBITRARY AUTOMATION, so it is refused unless the server operator set UiPathCli:EnableExecution=true. Prefer this over run_workflow for UI automation: the app is preserved for selector repair on error. READ DebugState BEFORE HasErrors — a Suspended session has an awaiting exception while HasErrors is still false, and Paused means a breakpoint was hit with the current activity and locals in debugDetails. Every mid-session command returns at the next stable state (Paused, Suspended, Running when the wait timed out, or Completed). command=start needs filePath; breakpoints target activities by their sap2010:WorkflowViewState.IdRef (workflowFile=Main.xaml,activityIdRef=Assign_1). command=cancel ends the active run or session — always cancel when done. Pass profiling=true on start to surface profilingOutputDirectory (the .uistat files and screenshots). Next: validate_project.")]
    public async Task<ToolResult> ControlDebugSession(
        [Description("Absolute path to the UiPath project directory (must contain project.json).")] string projectPath,
        [Description("Debug command: start, state, step-over, step-into, step-out, continue, continue-retry, continue-ignore, resume, break, restart-from-top, set-breakpoints, or cancel.")]
        [AllowedValues(
            StartCommand,
            CliVerbArguments.DebugStateCommand,
            CliVerbArguments.DebugStepOverCommand,
            CliVerbArguments.DebugStepIntoCommand,
            CliVerbArguments.DebugStepOutCommand,
            CliVerbArguments.DebugContinueCommand,
            CliVerbArguments.DebugContinueRetryCommand,
            CliVerbArguments.DebugContinueIgnoreCommand,
            CliVerbArguments.DebugResumeCommand,
            CliVerbArguments.DebugBreakCommand,
            CliVerbArguments.DebugRestartFromTopCommand,
            SetBreakpointsCommand,
            CliVerbArguments.CancelCommand)] string command,
        [Description("For command=start: workflow or coded file to debug, relative to the project root, e.g. 'Main.xaml'.")] string? filePath = null,
        [Description("For command=start: repeatable input arguments ('name=John', 'retries:=3', 'payload=@file.json'). Values containing double quotes are rejected — use key=@file.")] List<string>? inputArguments = null,
        [Description("For command=start or set-breakpoints: repeatable breakpoints as comma-joined key=value items, e.g. 'workflowFile=Main.xaml,activityIdRef=Assign_1' or with condition/hitCount/enabled. Replaces the whole set for set-breakpoints.")] List<string>? breakpoints = null,
        [Description("For mid-session commands: maximum seconds to wait for the next stable state before returning DebugState 'Running' (default 120, 0 for an instant probe on command=state).")] int? waitTimeoutSeconds = null,
        [Description("For command=start: minimum workflow log level (Verbose, Trace, Information, Warning, Error, Critical).")]
        [AllowedValues(
            CliVerbArguments.LogLevelVerbose,
            CliVerbArguments.LogLevelTrace,
            CliVerbArguments.LogLevelInformation,
            CliVerbArguments.LogLevelWarning,
            CliVerbArguments.LogLevelError,
            CliVerbArguments.LogLevelCritical)] string? logLevel = null,
        [Description("For command=start: skip validation and build, assuming the project was already built.")] bool skipBuild = false,
        [Description("For command=start: collect per-activity profiling and return profilingOutputDirectory (.uistat files and screenshots). Requires a Studio Develop profile with EnableProfiling.")] bool profiling = false,
        [Description("For command=start: profiling delivery mode, endOfRun (default) or stream.")]
        [AllowedValues(CliVerbArguments.ProfilingModeEndOfRun, CliVerbArguments.ProfilingModeStream)] string? profilingMode = null,
        [Description("Include the workflow's log entries in the response (default false). Logs are diagnostic context only — never a verdict.")] bool includeLogEntries = false,
        [Description("Optional CLI timeout in seconds (default 300, max 3600). Must exceed waitTimeoutSeconds by at least 30s or the CLI is killed before it can cancel cleanly.")] int? timeoutSeconds = null,
        CancellationToken cancellationToken = default) {

        var sw = Stopwatch.StartNew();

        if (ToolArgs.ParseChoice(command, "command", Commands, sw, out var parsedCommand) is { } commandError) {
            return commandError;
        }

        if (ToolResults.GuardProject(_filesystem, projectPath, sw) is { } projectFailure) {
            return projectFailure;
        }

        if (logLevel is not null
            && ToolArgs.ParseChoice(logLevel, "logLevel", CliVerbArguments.RunLogLevels, sw, out _) is { } logLevelFailure) {
            return logLevelFailure;
        }

        if (profilingMode is not null
            && ToolArgs.ParseChoice(profilingMode, "profilingMode", CliVerbArguments.ProfilingModes, sw, out _) is { } profilingModeFailure) {
            return profilingModeFailure;
        }

        string? relativePath = null;
        string[] tokens;

        if (string.Equals(parsedCommand, StartCommand, StringComparison.Ordinal)) {
            if (RunWorkflowTool.ResolveTarget(_filesystem, projectPath, filePath ?? string.Empty, sw, out _, out relativePath) is { } targetFailure) {
                return targetFailure!;
            }

            var arguments = inputArguments ?? [];
            if (CliToolSupport.ValidateInputArguments(arguments, sw) is { } argumentFailure) {
                return argumentFailure;
            }

            tokens = CliVerbArguments.WithRpaVerb(CliVerbArguments.DebugStart(
                projectPath, relativePath!, arguments, breakpoints, logLevel, skipBuild, profiling, profilingMode));
        } else if (string.Equals(parsedCommand, CancelCommand, StringComparison.Ordinal)) {
            tokens = CliVerbArguments.WithRpaVerb(CliVerbArguments.ExecutionCancel(projectPath));
        } else if (string.Equals(parsedCommand, SetBreakpointsCommand, StringComparison.Ordinal)) {
            if (breakpoints is null || breakpoints.Count == 0) {
                return ToolResults.Failure(new ToolError(
                    CliToolErrorCodes.InvalidArgument,
                    "command=set-breakpoints requires at least one breakpoints entry.",
                    "Pass 'workflowFile=Main.xaml,activityIdRef=Assign_1'. The whole existing set is replaced."), sw);
            }

            tokens = CliVerbArguments.WithRpaVerb(CliVerbArguments.DebugSetBreakpoints(projectPath, breakpoints));
        } else {
            tokens = CliVerbArguments.WithRpaVerb(CliVerbArguments.DebugCommand(projectPath, parsedCommand!, waitTimeoutSeconds));
        }

        // Execution gate: fail closed unless UiPathCli:EnableExecution is set.
        if (CliToolSupport.GuardExecution(_policy, tokens, sw, SuggestedTool) is { } executionFailure) {
            return executionFailure;
        }

        // The CLI timeout must exceed the mid-session wait by a margin, or the process is killed
        // before it can cancel cleanly. `start` ignores --wait-timeout-seconds entirely.
        var isMidSession = CliVerbArguments.DebugSessionCommands.Contains(parsedCommand, StringComparer.Ordinal);
        if (isMidSession
            && waitTimeoutSeconds is { } wait
            && CliToolSupport.ClampTimeout(timeoutSeconds) - wait < MinimumTimeoutMarginSeconds) {
            return ToolResults.Failure(new ToolError(
                CliToolErrorCodes.InvalidArgument,
                $"timeoutSeconds must exceed waitTimeoutSeconds ({wait}s) by at least {MinimumTimeoutMarginSeconds}s.",
                $"Pass timeoutSeconds >= {wait + MinimumTimeoutMarginSeconds}, or lower waitTimeoutSeconds, so the CLI can cancel cleanly instead of being killed."), sw);
        }

        var outcome = await _cli.RunStructuredAsync(
            CliVerbArguments.RpaVerb, tokens, projectPath,
            timeoutSeconds: CliToolSupport.ClampTimeout(timeoutSeconds), cancellationToken);

        return BuildResult(outcome, parsedCommand!, relativePath ?? filePath, profiling, includeLogEntries, sw);
    }

    internal const string SuggestedTool = "validate_project";
    internal const string StartCommand = "start";
    internal const string CancelCommand = CliVerbArguments.CancelCommand;
    internal const string SetBreakpointsCommand = "set-breakpoints";
    internal const int MinimumTimeoutMarginSeconds = 30;

    private static readonly string[] Commands =
    [
        StartCommand,
        .. CliVerbArguments.DebugSessionCommands,
        SetBreakpointsCommand,
        CancelCommand
    ];

    /// <summary>
    /// Shapes the debug response. DebugState is read before HasErrors: a Suspended session has an
    /// awaiting exception while HasErrors is still false, so the session outcome is undecided.
    /// </summary>
    internal static ToolResult BuildResult(
        UiPathCliRunResult outcome, string command, string? filePath,
        bool profilingRequested, bool includeLogEntries, Stopwatch sw) {
        var cli = outcome.Cli;
        if (outcome.Verdict is not { } verdict) {
            return CliToolSupport.CliFailure(outcome, cli.Summary, sw, SuggestedTool);
        }

        var warnings = new List<string>();
        var isCancel = string.Equals(command, CancelCommand, StringComparison.Ordinal);

        if (profilingRequested && string.IsNullOrWhiteSpace(verdict.ProfilingOutputDirectory)) {
            warnings.Add("Profiling was requested but no profilingOutputDirectory was returned. Only start verbs collect it, and it needs a Studio Develop profile with EnableProfiling set.");
        }

        if (verdict.LogEntriesTruncated) {
            warnings.Add($"logEntries was truncated to the first {verdict.LogEntries.Count} entries. Logs are diagnostic context only, never the verdict.");
        }

        if (!includeLogEntries && verdict.LogEntries.Count > 0) {
            warnings.Add($"{verdict.LogEntries.Count} log entries were omitted; pass includeLogEntries=true to read them.");
        }

        if (verdict.IsSuspended) {
            warnings.Add("DebugState is Suspended and HasErrors is false: an exception is awaiting your decision, the run outcome is NOT decided. Send continue-retry (transient fault), continue-ignore (may leave variables inconsistent), or continue, then cancel.");
        }

        if (verdict.IsPaused) {
            warnings.Add("DebugState is Paused at a breakpoint: read the current activity and locals in debugDetails, then send command=step-over, step-into, or continue.");
        }

        if (verdict.IsRunning) {
            warnings.Add("DebugState is Running: the wait timed out before a stable state. Poll command=state, or send command=break to stop at the next activity.");
        }

        if (!isCancel && !verdict.IsCompleted && !verdict.IsRunning) {
            warnings.Add("The session is still alive. Send command=cancel when done so the run or session ends cleanly.");
        }

        return new ToolResult {
            Status = verdict.Succeeded || verdict.IsAwaitingDecision ? "success" : "error",
            Summary = Summarize(command, filePath, verdict),
            Data = CliToolSupport.VerdictPayload(verdict, cli.Command, cli.ExitCode, includeLogEntries),
            Errors = verdict.Succeeded || verdict.IsAwaitingDecision ? [] : FailureEntries(verdict, cli),
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        };
    }

    private static string Summarize(string command, string? filePath, CliRunResult verdict) {
        var label = string.IsNullOrWhiteSpace(filePath) ? command : $"{command} ({filePath})";

        if (verdict.IsSuspended) {
            return $"{label}: DebugState Suspended — an exception awaits a decision (HasErrors is still false).";
        }

        if (verdict.IsPaused) {
            return $"{label}: DebugState Paused — inspect debugDetails, then step or continue.";
        }

        if (verdict.IsRunning) {
            return $"{label}: DebugState Running — no stable state within the wait.";
        }

        return verdict.Succeeded
            ? $"{label}: completed successfully."
            : $"{label}: failed — {FirstReason(verdict)}";
    }

    private static string FirstReason(CliRunResult verdict) {
        if (!string.IsNullOrWhiteSpace(verdict.ErrorMessage)) {
            return CliToolSupport.Truncate(verdict.ErrorMessage!, 240);
        }

        if (verdict.Errors.Count > 0) {
            var first = verdict.Errors[0];
            return CliToolSupport.Truncate(
                string.IsNullOrWhiteSpace(first.ErrorName)
                    ? first.ErrorMessage ?? "execution error"
                    : $"{first.ErrorName}: {first.ErrorMessage}", 240);
        }

        if (!string.IsNullOrWhiteSpace(verdict.Output)) {
            return CliToolSupport.Truncate(verdict.Output!, 240);
        }

        return !verdict.EnvelopeSuccess
            ? CliToolSupport.Truncate(verdict.Message ?? $"the CLI returned result '{verdict.Result}'", 240)
            : "no clean completion was reported.";
    }

    private static List<string> FailureEntries(CliRunResult verdict, UiPathCliResult cli) {
        var entries = new List<string> {
            verdict.DebugState is not null
                ? $"[debug] debugState={verdict.DebugState}: {FirstReason(verdict)}"
                : $"[debug] {FirstReason(verdict)}"
        };

        foreach (var error in verdict.Errors.Skip(1).Take(9)) {
            entries.Add($"[debug] {error.ErrorName ?? "ERROR"}: {error.ErrorMessage}");
        }

        entries.AddRange(cli.Errors.Where(e => !entries.Contains(e, StringComparer.Ordinal)));
        return entries;
    }
}
