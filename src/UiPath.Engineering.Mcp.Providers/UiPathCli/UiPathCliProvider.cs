using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using UiPath.Engineering.Mcp.Core;
using UiPath.Engineering.Mcp.Core.Configuration;
using UiPath.Engineering.Mcp.Core.Parsing;

namespace UiPath.Engineering.Mcp.Providers.UiPathCli;

public sealed class UiPathCliProvider : IUiPathCliProvider {
    private readonly UiPathCliOptions _options;
    private readonly ILogger<UiPathCliProvider> _logger;
    private readonly CliCommandPolicy _policy;

    // Resolved once (the provider is a singleton); Lazy<> is thread-safe by default.
    private readonly Lazy<CliExecutableResolver.LaunchSpec?> _launchSpec;

    public UiPathCliProvider(IOptions<UiPathCliOptions> options, ILogger<UiPathCliProvider>? logger = null) {
        _options = options.Value;
        _logger = logger ?? NullLogger<UiPathCliProvider>.Instance;
        _policy = new CliCommandPolicy(_options);
        _launchSpec = new Lazy<CliExecutableResolver.LaunchSpec?>(
            () => CliExecutableResolver.Resolve(_options.ExecutablePath));
    }

    public async Task<UiPathCliResult> ValidateAsync(
        string projectPath,
        bool validate,
        bool build,
        bool pack,
        CancellationToken cancellationToken = default) {
        // The npm CLI (uip 1.x, @uipath/cli) has no restore/analyze verbs: "rpa validate"
        // returns project diagnostics, "rpa build" is the compile gate (NuGet included),
        // "rpa pack" produces the package. One verb per invocation, so each requested step
        // runs sequentially and the results are aggregated into a single structured response.
        var errors = new List<string>();
        var warnings = new List<string>();
        var diagnostics = new List<CliDiagnostic>();
        var rawOutput = new List<string>();
        var executedCommands = new List<string>();
        var overallSuccess = true;
        var lastExitCode = 0;

        if (CliCommandPolicy.ContainsRejectedChars(projectPath)) {
            return new UiPathCliResult {
                Success = false,
                ExitCode = -1,
                Summary = "Project path rejected.",
                Errors = ["The project path contains control characters and cannot be passed as a process argument."]
            };
        }

        var steps = new List<(string Verb, bool Enabled)>
        {
            ("validate", validate),
            ("build", build),
            ("pack", pack)
        };

        // Steps that are not requested, or skipped after an earlier failure, keep
        // Executed = false so callers can distinguish "not run" from "ran clean".
        var stepResults = new Dictionary<string, CliStepResult> {
            ["validate"] = new CliStepResult(),
            ["build"] = new CliStepResult(),
            ["pack"] = new CliStepResult()
        };

        foreach (var (verb, enabled) in steps) {
            if (!enabled) {
                continue;
            }

            var (stepResult, _) = await RunTokensAsync(verb, BuildVerbArguments(verb, projectPath), null, cancellationToken);

            stepResults[verb] = new CliStepResult {
                Executed = true,
                Success = stepResult.Success,
                Errors = stepResult.Errors,
                Warnings = stepResult.Warnings,
                Diagnostics = stepResult.Diagnostics
            };

            executedCommands.Add(stepResult.Command);
            errors.AddRange(stepResult.Errors);
            warnings.AddRange(stepResult.Warnings);
            diagnostics.AddRange(stepResult.Diagnostics);
            rawOutput.AddRange(stepResult.RawOutputLines);
            lastExitCode = stepResult.ExitCode;

            if (!stepResult.Success) {
                overallSuccess = false;
                // Stop the pipeline on the first failing step (e.g. a failed validate
                // should not be followed by build/pack against a broken state).
                break;
            }
        }

        return new UiPathCliResult {
            Success = overallSuccess,
            Command = string.Join(" && ", executedCommands),
            ExitCode = lastExitCode,
            Summary = overallSuccess ? "Validation completed." : "Validation failed.",
            Validate = stepResults["validate"],
            Build = stepResults["build"],
            Pack = stepResults["pack"],
            Errors = errors,
            Warnings = warnings,
            Diagnostics = diagnostics,
            RawOutputLines = _options.IncludeRawOutput ? rawOutput : []
        };
    }

    // Command tokens per the uip 1.x rpa surface: validate takes --project-dir, build and
    // pack take the directory positionally; --output json so the output parser can read
    // the structured response envelope. The project path is one ArgumentList token.
    internal static string[] BuildVerbArguments(string verb, string projectPath) => verb switch {
        "validate" => ["rpa", "validate", "--project-dir", projectPath, "--output", "json"],
        "build" => ["rpa", "build", projectPath, "--output", "json"],
        _ => ["rpa", "pack", projectPath, "--output", "json"]
    };

    // Redacts secrets and caps each stream so tool responses stay bounded.
    internal static (string StdOut, string StdErr) CaptureOutput(string stdout, string stderr, int maxChars) {
        var (redactedOut, _) = SecretRedactor.Redact(stdout);
        var (redactedErr, _) = SecretRedactor.Redact(stderr);
        return (Cap(redactedOut, maxChars), Cap(redactedErr, maxChars));
    }

    internal static List<string> BuildRawOutputLines(string stdout, string stderr) {
        var lines = new List<string>();
        lines.AddRange(SecretRedactor.RedactLines(ProcessRunner.SplitLines(stdout)));
        lines.AddRange(SecretRedactor.RedactLines(ProcessRunner.SplitLines(stderr)));
        return lines;
    }

    private static string Cap(string s, int maxChars) =>
        s.Length <= maxChars ? s : s[..maxChars] + "\n...[truncated]";

    public async Task<UiPathCliResult> RunAsync(
        string verb,
        string arguments,
        string? workingDirectory = null,
        CancellationToken cancellationToken = default) {
        if (CliCommandPolicy.ContainsRejectedChars(arguments)) {
            return new UiPathCliResult {
                Success = false,
                Command = $"{_options.ExecutablePath} {arguments}",
                ExitCode = -1,
                Summary = "Arguments rejected.",
                Errors = ["The arguments contain control characters that cannot be passed as process arguments."]
            };
        }

        var (result, _) = await RunTokensAsync(
            verb, ProcessRunner.SplitQuotedArguments(arguments), workingDirectory, cancellationToken);
        return result;
    }

    /// <summary>
    /// Refuses an execution verb (rpa run / debug / execution) unless
    /// <see cref="UiPathCliOptions.EnableExecution"/> is set. Applied on every entry point that
    /// starts a process, so no caller — including the run_ui_path_cli hatch — can execute
    /// automation on this machine through a path that skips the gate.
    /// </summary>
    internal UiPathCliResult? ExecutionRefusal(string verb, string arguments) {
        if (!_policy.IsExecutionCommand(verb, arguments)) {
            return null;
        }

        _logger.LogInformation(
            "UiPath CLI {Verb} duration {DurationMs}ms status {Status} errorCode {ErrorCode}",
            verb, 0, "error", "execution_disabled");

        return new UiPathCliResult {
            Success = false,
            Command = $"{_options.ExecutablePath} {arguments}",
            ExitCode = -1,
            Summary = "Workflow execution is disabled on this server.",
            Errors =
            [
                "Running or debugging a workflow executes arbitrary automation on this machine, so it is disabled by default.",
                "Set UiPathCli:EnableExecution to true in appsettings.json and restart the server."
            ]
        };
    }

    /// <summary>
    /// Token-faithful structured invocation: no re-tokenizing (so a value containing spaces or
    /// quotes stays one ArgumentList entry), the response envelope parsed from the UNCAPPED
    /// stdout, and an execution gate on the rpa run / debug / execution verbs.
    /// </summary>
    public async Task<UiPathCliRunResult> RunStructuredAsync(
        string verb,
        IReadOnlyList<string> tokens,
        string? workingDirectory = null,
        int? timeoutSeconds = null,
        CancellationToken cancellationToken = default) {
        if (tokens.Count == 0) {
            return new UiPathCliRunResult {
                Refused = true,
                Cli = new UiPathCliResult {
                    Success = false,
                    ExitCode = -1,
                    Summary = "No CLI command tokens supplied.",
                    Errors = ["At least one command token is required."]
                }
            };
        }

        if (tokens.Any(CliCommandPolicy.ContainsRejectedChars)) {
            return new UiPathCliRunResult {
                Refused = true,
                Cli = new UiPathCliResult {
                    Success = false,
                    ExitCode = -1,
                    Summary = "Arguments rejected.",
                    Errors = ["The command contains control characters that cannot be passed as process arguments."]
                }
            };
        }

        // Running a workflow executes arbitrary automation on this machine, so it is gated by a
        // dedicated fail-closed switch rather than EnableMutatingCommands.
        if (IsExecutionRefused(verb, tokens)) {
            _logger.LogInformation(
                "UiPath CLI {Verb} duration {DurationMs}ms status {Status} errorCode {ErrorCode}",
                verb, 0, "error", "execution_disabled");
            return new UiPathCliRunResult {
                Refused = true,
                Cli = new UiPathCliResult {
                    Success = false,
                    Command = FormatExecutedCommand(_options.ExecutablePath, tokens),
                    ExitCode = -1,
                    Summary = "Workflow execution is disabled on this server.",
                    Errors =
                    [
                        "Running or debugging a workflow executes arbitrary automation on this machine, so it is disabled by default.",
                        "Set UiPathCli:EnableExecution to true in appsettings.json and restart the server."
                    ]
                }
            };
        }

        var (cli, fullStdOut) = await RunTokensAsync(
            verb, tokens, workingDirectory, cancellationToken, timeoutSeconds, captureFullStdOut: true);

        // The envelope is parsed from the UNCAPPED stdout so a long run response is never
        // truncated into unparseable JSON. It is deliberately parsed un-redacted: SecretRedactor's
        // key=value rule rewrites to end-of-line, which would corrupt a JSON payload and lose the
        // verdict. Redaction happens where the text is rendered for display — Cli.StdOut/StdErr
        // here, and the individual message fields in the tool payload.
        var parseSource = string.IsNullOrWhiteSpace(fullStdOut) ? cli.StdOut : fullStdOut;

        return new UiPathCliRunResult {
            Cli = cli,
            Envelope = CliEnvelopeParser.TryParse(parseSource, out var envelope) ? envelope : null
        };
    }

    private bool IsExecutionRefused(string verb, IReadOnlyList<string> tokens) =>
        !_policy.ExecutionEnabled
        && _policy.IsExecutionCommand(verb, CliVerbArguments.ToArgumentString(tokens));

    private Task<(UiPathCliResult Cli, string FullStdOut)> RunTokensAsync(
        string verb,
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        CancellationToken cancellationToken) =>
        RunTokensCoreAsync(verb, arguments, workingDirectory, timeoutSeconds: null, cancellationToken, captureFullStdOut: false);

    private Task<(UiPathCliResult Cli, string FullStdOut)> RunTokensAsync(
        string verb,
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        CancellationToken cancellationToken,
        int? timeoutSeconds,
        bool captureFullStdOut) =>
        RunTokensCoreAsync(verb, arguments, workingDirectory, timeoutSeconds, cancellationToken, captureFullStdOut);

    private async Task<(UiPathCliResult Cli, string FullStdOut)> RunTokensCoreAsync(
        string verb,
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        int? timeoutSeconds,
        CancellationToken cancellationToken,
        bool captureFullStdOut) {
        // Execution backstop at the single process-start choke point. The verb tools gate before
        // they build their command, but this keeps the guarantee provider-wide: no path through
        // RunAsync / RunStructuredAsync can start `rpa run`, `rpa debug`, or `rpa execution`
        // unless EnableExecution is set.
        if (!_policy.ExecutionEnabled
            && ExecutionRefusal(verb, CliVerbArguments.ToArgumentString(arguments)) is { } refusal) {
            return (refusal, string.Empty);
        }

        var spec = _launchSpec.Value;
        if (spec is null) {
            var baseName = Path.GetFileNameWithoutExtension(_options.ExecutablePath);
            _logger.LogInformation(
                "UiPath CLI {Verb} duration {DurationMs}ms status {Status} errorCode {ErrorCode}",
                verb, 0, "error", "cli_not_found");
            return (new UiPathCliResult {
                Success = false,
                Command = FormatExecutedCommand(_options.ExecutablePath, arguments),
                ExitCode = -1,
                Summary = $"UiPath CLI ('{_options.ExecutablePath}') not found.",
                Errors =
                [
                    $"The UiPath CLI ('{_options.ExecutablePath}') was not found on PATH (searched for {baseName}.exe, {baseName}.cmd, {baseName}.bat, {baseName}.ps1).",
                    "Install it (npm install -g @uipath/cli) or set UiPathCli:ExecutablePath in appsettings.json."
                ]
            }, string.Empty);
        }

        var command = FormatExecutedCommand(spec.ResolvedPath, arguments);
        var sw = Stopwatch.StartNew();
        var timeout = TimeSpan.FromSeconds(timeoutSeconds ?? _options.DefaultTimeoutSeconds);

        var run = await ProcessRunner.RunAsync(
            spec.FileName, spec.BuildArgumentList(arguments), workingDirectory,
            timeout, cancellationToken,
            EffectiveEnvironment());

        sw.Stop();

        if (run.StartError is not null) {
            // Most common cause: resolved shim or its host (cmd.exe/powershell.exe) failed to start.
            _logger.LogInformation(
                "UiPath CLI {Verb} duration {DurationMs}ms status {Status} errorCode {ErrorCode}",
                verb, sw.ElapsedMilliseconds, "error", "start_error");
            return (new UiPathCliResult {
                Success = false,
                Command = command,
                ExitCode = -1,
                Summary = $"Failed to start '{spec.FileName}'.",
                Errors =
                [
                    $"Could not start the UiPath CLI ('{spec.ResolvedPath}'): {run.StartError}",
                    "Verify that the UiPath CLI ('uip') is installed (npm install -g @uipath/cli) and available on PATH, or set UiPathCli:ExecutablePath in appsettings.json."
                ]
            }, string.Empty);
        }

        if (run.Canceled) {
            _logger.LogInformation(
                "UiPath CLI {Verb} duration {DurationMs}ms status {Status} errorCode {ErrorCode}",
                verb, sw.ElapsedMilliseconds, "canceled", "canceled");
            return (new UiPathCliResult {
                Success = false,
                Command = command,
                ExitCode = -1,
                Summary = $"CLI '{verb}' was canceled.",
                Errors = [$"'{verb}' was canceled by the caller."]
            }, string.Empty);
        }

        if (run.TimedOut) {
            _logger.LogInformation(
                "UiPath CLI {Verb} duration {DurationMs}ms status {Status} errorCode {ErrorCode}",
                verb, sw.ElapsedMilliseconds, "error", "timeout");
            return (new UiPathCliResult {
                Success = false,
                Command = command,
                ExitCode = -1,
                Summary = $"CLI '{verb}' execution timed out.",
                Errors = [$"'{verb}' exceeded the {(int)timeout.TotalSeconds}s timeout."]
            }, string.Empty);
        }

        var parsed = UiPathCliOutputParser.Parse(verb, run.StdOut, run.StdErr);
        var errors = parsed.Errors;
        var warnings = parsed.Warnings;

        if (run.ExitCode != 0 && errors.Count == 0) {
            // The process failed without emitting any recognizable error line;
            // still surface a minimal reason instead of a bare "failed".
            errors.Add($"[{verb}] '{verb}' exited with code {run.ExitCode}.");
        }

        var rawLines = _options.IncludeRawOutput
            ? BuildRawOutputLines(run.StdOut, run.StdErr)
            : [];

        var (stdout, stderr) = CaptureOutput(run.StdOut, run.StdErr, _options.MaxOutputChars);
        _logger.LogInformation(
            "UiPath CLI {Verb} duration {DurationMs}ms status {Status} errorCode {ErrorCode} exitCode {ExitCode}",
            verb,
            sw.ElapsedMilliseconds,
            run.ExitCode == 0 && errors.Count == 0 ? "success" : "error",
            run.ExitCode == 0 && errors.Count == 0 ? null : "exit",
            run.ExitCode);

        var success = run.ExitCode == 0 && errors.Count == 0;
        return (new UiPathCliResult {
            Success = success,
            Command = command,
            ExitCode = run.ExitCode,
            Summary = success ? $"'{verb}' completed." : $"'{verb}' failed.",
            Errors = errors,
            Warnings = warnings,
            Diagnostics = parsed.Diagnostics,
            RawOutputLines = rawLines,
            StdOut = stdout,
            StdErr = stderr
        }, captureFullStdOut ? run.StdOut : string.Empty);
    }

    internal static string FormatExecutedCommand(string resolvedPath, IReadOnlyList<string> arguments) {
        if (arguments.Count == 0) {
            return resolvedPath;
        }

        var parts = new string[arguments.Count + 1];
        parts[0] = resolvedPath;
        for (var i = 0; i < arguments.Count; i++) {
            var arg = arguments[i];
            parts[i + 1] = arg.Length == 0 || arg.Any(char.IsWhiteSpace) ? $"\"{arg}\"" : arg;
        }
        return string.Join(" ", parts);
    }

    /// <summary>
    /// Best-effort <c>uip --version</c> so the first Copilot CLI call does not pay cold start.
    /// Failures are ignored.
    /// </summary>
    public async Task WarmupAsync(CancellationToken cancellationToken = default) {
        try {
            await RunTokensCoreAsync(
                "version",
                ["--version"],
                workingDirectory: null,
                timeoutSeconds: 60,
                cancellationToken,
                captureFullStdOut: false).ConfigureAwait(false);
        } catch {
            // Fire-and-forget warm-up: ignore all failures.
        }
    }

    private IReadOnlyDictionary<string, string> EffectiveEnvironment() {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (_options.Environment is not null) {
            foreach (var (key, value) in _options.Environment) {
                map[key] = value;
            }
        }

        // Skip npm's update-notifier hang; no UiPath-documented UIPATH_* skip flag found for @uipath/cli.
        map.TryAdd("npm_config_update_notifier", "false");
        return map;
    }
}
