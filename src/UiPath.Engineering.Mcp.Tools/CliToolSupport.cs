using System.Diagnostics;
using System.Text.RegularExpressions;
using ModelContextProtocol;
using UiPath.Engineering.Mcp.Core;
using UiPath.Engineering.Mcp.Core.Abstractions;
using UiPath.Engineering.Mcp.Core.Models;
using UiPath.Engineering.Mcp.Providers.UiPathCli;

namespace UiPath.Engineering.Mcp.Tools;

/// <summary>
/// Error codes for the CLI-verb tools. Same taxonomy and stability contract as
/// <see cref="ToolErrorCodes"/>; kept in a separate file so the shared constants stay owned by
/// one worker at a time.
/// </summary>
internal static class CliToolErrorCodes {
    /// <summary>Automation execution is gated off by <c>UiPathCli:EnableExecution</c>.</summary>
    public const string ExecutionDisabled = "EXECUTION_DISABLED";

    /// <summary>The UiPath CLI is not installed or not resolvable on PATH.</summary>
    public const string CliUnavailable = "CLI_UNAVAILABLE";

    /// <summary>The CLI responded with a payload this server could not read.</summary>
    public const string CliUnparseableResponse = "CLI_UNPARSEABLE_RESPONSE";

    public const string MutatingCommandDisabled = ToolErrorCodes.MutatingCommandDisabled;
    public const string InvalidArgument = ToolErrorCodes.InvalidArgument;
    public const string CliArgumentsRejected = ToolErrorCodes.CliArgumentsRejected;
    public const string OperationFailed = ToolErrorCodes.OperationFailed;
}

/// <summary>
/// Shared guards and payload shaping for tools that drive a specific <c>uip rpa</c> verb.
/// Every call goes through <see cref="IUiPathCliProvider.RunStructuredAsync"/>, so arguments are
/// ArgumentList tokens (never a concatenated shell string) and the response envelope is parsed
/// from the uncapped, redacted stdout.
/// </summary>
internal static class CliToolSupport {
    /// <summary>
    /// Accepted <c>--input-arguments</c> item forms: <c>key=value</c>, <c>key:=value</c> (raw JSON),
    /// <c>key=@file</c>, or a bare <c>@file</c>. Anchored as one alternation so a bare identifier
    /// with no operator does not match.
    /// </summary>
    private static readonly Regex InputArgumentItem = new(
        @"^(?:[A-Za-z_]\w*(?::=|=).*|@\S.*)$",
        RegexOptions.Compiled);

    /// <summary>Default bound on the CLI wait so a cold headless Studio restore is not killed early.</summary>
    public const int DefaultTimeoutSeconds = 300;

    public const int MaxTimeoutSeconds = 3600;

    public static int ClampTimeout(int? requested) =>
        requested is { } seconds && seconds > 0 ? Math.Min(seconds, MaxTimeoutSeconds) : DefaultTimeoutSeconds;

    /// <summary>
    /// Refuses an execution verb unless <c>UiPathCli:EnableExecution</c> is set. Running a workflow
    /// executes arbitrary automation on this machine, so the gate is fail-closed and separate from
    /// EnableMutatingCommands. The caller guards the project directory first.
    /// </summary>
    /// <param name="tokens">Command tokens including the leading top-level verb ("rpa").</param>
    public static ToolResult? GuardExecution(
        CliCommandPolicy policy,
        IReadOnlyList<string> tokens,
        Stopwatch sw,
        string suggestedTool) {
        if (!IsExecution(policy, tokens) || policy.ExecutionEnabled) {
            return null;
        }

        return ToolResults.Failure("Workflow execution is disabled on this server.",
            [new ToolError(
                CliToolErrorCodes.ExecutionDisabled,
                "Running or debugging a workflow executes arbitrary automation on this machine, so it is disabled by default.",
                "Set UiPathCli:EnableExecution to true in appsettings.json and restart the server. While it stays off, use validate_project(build:true) as the compilability gate.",
                suggestedTool)], sw);
    }

    /// <summary>
    /// Guards the project directory, then refuses a mutating CLI subcommand unless
    /// <c>UiPathCli:EnableMutatingCommands</c> is set. Subcommands listed in
    /// <c>UiPathCli:ReadOnlySubcommands</c> pass through; anything unlisted fails closed.
    /// </summary>
    /// <param name="tokens">Command tokens including the leading top-level verb ("rpa").</param>
    public static ToolResult? GuardSubcommand(
        IFilesystemProvider filesystem,
        CliCommandPolicy policy,
        string projectPath,
        IReadOnlyList<string> tokens,
        Stopwatch sw) {
        if (ToolResults.GuardProject(filesystem, projectPath, sw) is { } projectFailure) {
            return projectFailure;
        }

        return ClassifySubcommand(policy, tokens, sw);
    }

    /// <summary>
    /// The mutating/allowlist gate for a token list, without the project-directory guard (for
    /// callers that already guarded the project).
    /// </summary>
    public static ToolResult? ClassifySubcommand(CliCommandPolicy policy, IReadOnlyList<string> tokens, Stopwatch sw) {
        if (tokens.Count < 2) {
            return null;
        }

        // tokens[0] is the top-level verb; the policy classifies the subcommand that follows it.
        var classification = policy.Classify(tokens[0], CliVerbArguments.SubcommandArguments(tokens));

        return classification switch {
            CliCommandClass.VerbNotAllowed => ToolResults.Failure($"Verb '{tokens[0]}' is not allowed.",
                [new ToolError(
                    ToolErrorCodes.CliVerbNotAllowed,
                    $"The verb '{tokens[0]}' is not in the server allowlist.",
                    "Ask the server operator to add it to UiPathCli:AllowedVerbs.")], sw),
            CliCommandClass.ArgumentsRejected => ToolResults.Failure("Arguments rejected.",
                [new ToolError(
                    ToolErrorCodes.CliArgumentsRejected,
                    "The command contains control characters that cannot be passed as process arguments.",
                    "Remove newlines and other control characters from the values.")], sw),
            CliCommandClass.AllowedMutating when !policy.MutatingEnabled => MutatingRefusal(tokens, sw),
            _ => null
        };
    }

    private static bool IsExecution(CliCommandPolicy policy, IReadOnlyList<string> tokens) =>
        tokens.Count >= 2 && policy.IsExecution(tokens[0], CliVerbArguments.SubcommandArguments(tokens));

    private static ToolResult MutatingRefusal(IReadOnlyList<string> tokens, Stopwatch sw) {
        var subcommand = string.Join(' ', tokens.Skip(1).TakeWhile(t => !t.StartsWith('-')));
        return ToolResults.Failure("Mutating command blocked.",
            [new ToolError(
                CliToolErrorCodes.MutatingCommandDisabled,
                $"'{subcommand}' is classified as mutating and mutating commands are disabled on this server.",
                "Set UiPathCli:EnableMutatingCommands to true in appsettings.json and restart the server.")], sw);
    }

    /// <summary>
    /// Validates caller-supplied <c>--input-arguments</c> items. Returns a failure when an item is
    /// malformed or cannot survive the Windows PowerShell 5.1 shim (it strips double quotes, so
    /// quote-bearing values must travel through a file instead).
    /// </summary>
    public static ToolResult? ValidateInputArguments(IReadOnlyList<string> items, Stopwatch sw) {
        foreach (var item in items) {
            if (string.IsNullOrWhiteSpace(item)) {
                return ToolResults.Failure(new ToolError(
                    CliToolErrorCodes.InvalidArgument,
                    "An inputArguments entry is empty.",
                    "Pass entries as key=value, key:=value (raw JSON), key=@file, or @file."), sw);
            }

            if (CliCommandPolicy.ContainsRejectedChars(item)) {
                return ToolResults.Failure(new ToolError(
                    CliToolErrorCodes.CliArgumentsRejected,
                    "An inputArguments entry contains control characters.",
                    "Remove newlines; write the value to a UTF-8 file and pass key=@file."), sw);
            }

            if (item.Contains('"')) {
                return ToolResults.Failure(new ToolError(
                    CliToolErrorCodes.InvalidArgument,
                    $"The inputArguments entry '{Truncate(item)}' contains a double quote.",
                    "Windows PowerShell 5.1 strips double quotes from inline arguments. Write the value to a UTF-8 file and pass key=@file (or @file)."), sw);
            }

            if (!InputArgumentItem.IsMatch(item)) {
                return ToolResults.Failure(new ToolError(
                    CliToolErrorCodes.InvalidArgument,
                    $"The inputArguments entry '{Truncate(item)}' is not a key=value pair.",
                    "Pass key=value (string), key:=value (raw JSON), key=@file (value from a file), or @file (whole payload from a file)."), sw);
            }
        }

        return null;
    }

    /// <summary>Maps a provider failure that produced no parseable envelope to a structured error.</summary>
    public static ToolResult CliFailure(UiPathCliRunResult outcome, string summary, Stopwatch sw, string suggestedTool) {
        var cli = outcome.Cli;
        var details = new List<ToolError>();

        if (cli.ExitCode == -1
            && cli.Errors.Any(e => e.Contains("not found on PATH", StringComparison.Ordinal))) {
            details.Add(new ToolError(
                CliToolErrorCodes.CliUnavailable,
                "The UiPath CLI ('uip') is not installed or not resolvable on PATH.",
                "Install it (npm install -g @uipath/cli) or set UiPathCli:ExecutablePath in appsettings.json.",
                suggestedTool));
        } else if (cli.Success) {
            details.Add(new ToolError(
                CliToolErrorCodes.CliUnparseableResponse,
                "The CLI returned a response this server could not parse as an envelope.",
                "Retry once; if it persists, raise UiPathCli:DefaultTimeoutSeconds (a cold headless Studio restore takes 30-90s).",
                suggestedTool));
        }

        if (details.Count == 0) {
            return ToolResults.Failure(summary, cli.Errors.Count > 0 ? cli.Errors : [summary], sw);
        }

        return ToolResults.Failure(summary, details, sw);
    }

    /// <summary>Maps a parsed envelope whose Result is not Success to a structured error.</summary>
    public static ToolResult EnvelopeFailure(
        CliEnvelope envelope, string fallbackSummary, Stopwatch sw, string suggestedTool, string fixHint) {
        var message = string.Join(" ", new[] { envelope.Message, envelope.Instructions }
            .Where(s => !string.IsNullOrWhiteSpace(s)));
        var text = message.Length > 0
            ? message
            : envelope.Result is { } result
                ? $"The CLI returned result '{result}'."
                : fallbackSummary;

        return ToolResults.Failure(text, [new ToolError(
            CliToolErrorCodes.OperationFailed, text, fixHint, suggestedTool)], sw);
    }

    /// <summary>
    /// Shapes a run/debug verdict. The outer Result and the inner HasErrors (or the flat shape's
    /// errors + output) are the only verdict sources: <see cref="CliRunResult.LogEntries"/> is
    /// diagnostic context, and a workflow that logs at Error level as observability still passes.
    /// </summary>
    public static object VerdictPayload(CliRunResult verdict, string command, int exitCode, bool includeLogs) {
        var logEntries = includeLogs
            ? verdict.LogEntries.Select(l => (object)new { source = l.Source, level = l.Level, message = l.Message }).ToList()
            : new List<object>();

        return new {
            succeeded = verdict.Succeeded,
            envelopeResult = verdict.Result,
            exitCode,
            hasErrors = verdict.HasErrors,
            errorMessage = verdict.ErrorMessage,
            output = verdict.Output,
            errors = verdict.Errors.Select(e => new {
                errorName = e.ErrorName,
                errorMessage = e.ErrorMessage,
                lineNumber = e.LineNumber
            }).ToList(),
            debugState = verdict.DebugState,
            debugDetails = verdict.DebugDetails,
            profilingOutputDirectory = verdict.ProfilingOutputDirectory,
            awaitingDecision = verdict.IsAwaitingDecision,
            payloadShape = verdict.UsedRunResultEnvelope ? "runResult" : "flat",
            command,
            logEntries,
            logEntriesTruncated = verdict.LogEntriesTruncated,
            verdictNote = VerdictNote(verdict)
        };
    }

    /// <summary>
    /// The reading order the CLI docs require: DebugState first (a Suspended session has an
    /// awaiting exception while HasErrors is still false), then HasErrors, then log context.
    /// </summary>
    public static string VerdictNote(CliRunResult verdict) {
        if (!verdict.EnvelopeSuccess) {
            return "The CLI invocation itself failed, so the workflow outcome is unknown. Read errors and errorMessage.";
        }

        if (verdict.IsSuspended) {
            return "DebugState is Suspended: an exception is awaiting a decision while HasErrors is still false. "
                + "Send control_debug_session with command continue-retry, continue-ignore, or continue, then cancel.";
        }

        if (verdict.IsPaused) {
            return "DebugState is Paused at a breakpoint. Inspect debugDetails, then step or continue.";
        }

        if (verdict.IsRunning) {
            return "DebugState is Running: the wait timed out before a stable state. Poll control_debug_session with command state.";
        }

        if (verdict.Succeeded) {
            return "Verdict is success, from the envelope Result cross-checked against HasErrors / errors / output. "
                + "A log entry's level is workflow data, never a failure signal.";
        }

        return "Verdict is failure, from the envelope Result cross-checked against HasErrors / errors / output. "
            + "Read errorMessage first, then the log entries for the root cause.";
    }

    public static string Truncate(string value, int max = 60) =>
        value.Length <= max ? value : value[..max] + "...";

    /// <summary>
    /// Reports progress for a CLI-backed tool call. A CLI invocation is the whole cost of these
    /// tools (24-91s, plus a ~22s Studio cold start), so a notification before the process starts
    /// and one when it returns is the feedback that tells the client the call is alive. Progress
    /// is scoped to the in-flight request's progress token, so it works over the stateless HTTP
    /// transport even though no unsolicited server-to-client message is possible there.
    /// </summary>
    /// <remarks>
    /// Constructed with the number of steps the tool will report. A null
    /// <paramref name="progress"/> is tolerated: the SDK only supplies one when the client sent a
    /// progress token, so a client that does not ask for progress gets no-ops.
    /// </remarks>
    public sealed class CliProgress {
        private readonly IProgress<ProgressNotificationValue>? _progress;
        private readonly int _total;
        private int _step;

        public CliProgress(IProgress<ProgressNotificationValue>? progress, int total) {
            _progress = progress;
            _total = total <= 0 ? 1 : total;
        }

        /// <summary>Reports the opening step, e.g. before the CLI process starts.</summary>
        public void Start(string message) => Report(_step, message);

        /// <summary>Advances one step and reports, e.g. when a CLI verb returns.</summary>
        public void Step(string message) {
            _step++;
            Report(_step, message);
        }

        private void Report(int value, string message) =>
            _progress?.Report(new ProgressNotificationValue {
                Progress = Math.Clamp(value, 0, _total),
                Total = _total,
                Message = message
            });
    }

    /// <summary>Starts a CLI progress scope for the common single-CLI-call tool.</summary>
    public static CliProgress ProgressFor(IProgress<ProgressNotificationValue>? progress, string startingMessage, int total = 2) {
        var scope = new CliProgress(progress, total);
        scope.Start(startingMessage);
        return scope;
    }
}
