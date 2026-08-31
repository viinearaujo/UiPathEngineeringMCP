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

    // Resolved once (the provider is a singleton); Lazy<> is thread-safe by default.
    private readonly Lazy<CliExecutableResolver.LaunchSpec?> _launchSpec;

    public UiPathCliProvider(IOptions<UiPathCliOptions> options, ILogger<UiPathCliProvider>? logger = null) {
        _options = options.Value;
        _logger = logger ?? NullLogger<UiPathCliProvider>.Instance;
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

            var stepResult = await RunTokensAsync(verb, BuildVerbArguments(verb, projectPath), null, cancellationToken);

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

    public Task<UiPathCliResult> RunAsync(
        string verb,
        string arguments,
        string? workingDirectory = null,
        CancellationToken cancellationToken = default) {
        if (CliCommandPolicy.ContainsRejectedChars(arguments)) {
            return Task.FromResult(new UiPathCliResult {
                Success = false,
                Command = $"{_options.ExecutablePath} {arguments}",
                ExitCode = -1,
                Summary = "Arguments rejected.",
                Errors = ["The arguments contain control characters that cannot be passed as process arguments."]
            });
        }

        return RunTokensAsync(verb, ProcessRunner.SplitQuotedArguments(arguments), workingDirectory, cancellationToken);
    }

    private async Task<UiPathCliResult> RunTokensAsync(
        string verb,
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        CancellationToken cancellationToken) {
        var spec = _launchSpec.Value;
        if (spec is null) {
            var baseName = Path.GetFileNameWithoutExtension(_options.ExecutablePath);
            _logger.LogInformation(
                "UiPath CLI {Verb} duration {DurationMs}ms status {Status} errorCode {ErrorCode}",
                verb, 0, "error", "cli_not_found");
            return new UiPathCliResult {
                Success = false,
                Command = FormatExecutedCommand(_options.ExecutablePath, arguments),
                ExitCode = -1,
                Summary = $"UiPath CLI ('{_options.ExecutablePath}') not found.",
                Errors =
                [
                    $"The UiPath CLI ('{_options.ExecutablePath}') was not found on PATH (searched for {baseName}.exe, {baseName}.cmd, {baseName}.bat, {baseName}.ps1).",
                    "Install it (npm install -g @uipath/cli) or set UiPathCli:ExecutablePath in appsettings.json."
                ]
            };
        }

        var command = FormatExecutedCommand(spec.ResolvedPath, arguments);
        var sw = Stopwatch.StartNew();

        var run = await ProcessRunner.RunAsync(
            spec.FileName, spec.BuildArgumentList(arguments), workingDirectory,
            TimeSpan.FromSeconds(_options.DefaultTimeoutSeconds), cancellationToken);

        sw.Stop();

        if (run.StartError is not null) {
            // Most common cause: resolved shim or its host (cmd.exe/powershell.exe) failed to start.
            _logger.LogInformation(
                "UiPath CLI {Verb} duration {DurationMs}ms status {Status} errorCode {ErrorCode}",
                verb, sw.ElapsedMilliseconds, "error", "start_error");
            return new UiPathCliResult {
                Success = false,
                Command = command,
                ExitCode = -1,
                Summary = $"Failed to start '{spec.FileName}'.",
                Errors =
                [
                    $"Could not start the UiPath CLI ('{spec.ResolvedPath}'): {run.StartError}",
                    "Verify that the UiPath CLI ('uip') is installed (npm install -g @uipath/cli) and available on PATH, or set UiPathCli:ExecutablePath in appsettings.json."
                ]
            };
        }

        if (run.Canceled) {
            _logger.LogInformation(
                "UiPath CLI {Verb} duration {DurationMs}ms status {Status} errorCode {ErrorCode}",
                verb, sw.ElapsedMilliseconds, "canceled", "canceled");
            return new UiPathCliResult {
                Success = false,
                Command = command,
                ExitCode = -1,
                Summary = $"CLI '{verb}' was canceled.",
                Errors = [$"'{verb}' was canceled by the caller."]
            };
        }

        if (run.TimedOut) {
            _logger.LogInformation(
                "UiPath CLI {Verb} duration {DurationMs}ms status {Status} errorCode {ErrorCode}",
                verb, sw.ElapsedMilliseconds, "error", "timeout");
            return new UiPathCliResult {
                Success = false,
                Command = command,
                ExitCode = -1,
                Summary = $"CLI '{verb}' execution timed out.",
                Errors = [$"'{verb}' exceeded the {_options.DefaultTimeoutSeconds}s timeout."]
            };
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
            run.ExitCode == 0 ? "success" : "error",
            run.ExitCode == 0 ? null : "exit",
            run.ExitCode);

        return new UiPathCliResult {
            Success = run.ExitCode == 0,
            Command = command,
            ExitCode = run.ExitCode,
            Summary = run.ExitCode == 0 ? $"'{verb}' completed." : $"'{verb}' failed.",
            Errors = errors,
            Warnings = warnings,
            Diagnostics = parsed.Diagnostics,
            RawOutputLines = rawLines,
            StdOut = stdout,
            StdErr = stderr
        };
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
}
