using Microsoft.Extensions.Options;
using UiPath.Engineering.Mcp.Core;
using UiPath.Engineering.Mcp.Core.Configuration;
using UiPath.Engineering.Mcp.Core.Parsing;

namespace UiPath.Engineering.Mcp.Providers.UiPathCli;

public sealed class UiPathCliProvider : IUiPathCliProvider {
    private readonly UiPathCliOptions _options;

    // Resolved once (the provider is a singleton); Lazy<> is thread-safe by default.
    private readonly Lazy<CliExecutableResolver.LaunchSpec?> _launchSpec;

    public UiPathCliProvider(IOptions<UiPathCliOptions> options) {
        _options = options.Value;
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

        // projectPath is interpolated into a cmd.exe /c command line; reject shell
        // metacharacters before any step runs (same rule CliCommandPolicy enforces
        // for run_ui_path_cli arguments).
        if (CliCommandPolicy.ContainsRejectedChars(projectPath)) {
            return new UiPathCliResult {
                Success = false,
                ExitCode = -1,
                Summary = "Project path rejected.",
                Errors = ["The project path contains shell metacharacters (& | < > % ^) and cannot be passed to the UiPath CLI safely."]
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

            var stepResult = await RunAsync(verb, BuildVerbArguments(verb, projectPath), null, cancellationToken);

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

    // Command lines per the uip 1.x rpa surface: validate takes --project-dir, build and
    // pack take the directory positionally; --output json so the output parser can read
    // the structured response envelope. Paths are quoted so directories with spaces
    // (e.g. OneDrive folders) work.
    internal static string BuildVerbArguments(string verb, string projectPath) => verb switch {
        "validate" => $"rpa validate --project-dir \"{projectPath}\" --output json",
        "build" => $"rpa build \"{projectPath}\" --output json",
        _ => $"rpa pack \"{projectPath}\" --output json"
    };

    // Redacts secrets and caps each stream so tool responses stay bounded.
    internal static (string StdOut, string StdErr) CaptureOutput(string stdout, string stderr, int maxChars) {
        var (redactedOut, _) = SecretRedactor.Redact(stdout);
        var (redactedErr, _) = SecretRedactor.Redact(stderr);
        return (Cap(redactedOut, maxChars), Cap(redactedErr, maxChars));
    }

    private static string Cap(string s, int maxChars) =>
        s.Length <= maxChars ? s : s[..maxChars] + "\n...[truncated]";

    public async Task<UiPathCliResult> RunAsync(
        string verb,
        string arguments,
        string? workingDirectory = null,
        CancellationToken cancellationToken = default) {
        // arguments are interpolated verbatim into a cmd.exe /c command line; reject
        // shell metacharacters here too, so a future caller that bypasses
        // CliCommandPolicy (run_ui_path_cli already classifies) cannot inject.
        if (CliCommandPolicy.ContainsRejectedChars(arguments)) {
            return new UiPathCliResult {
                Success = false,
                Command = $"{_options.ExecutablePath} {arguments}",
                ExitCode = -1,
                Summary = "Arguments rejected.",
                Errors = ["The arguments contain shell metacharacters (& | < > % ^) that could break out of the command shim."]
            };
        }

        var spec = _launchSpec.Value;
        if (spec is null) {
            var baseName = Path.GetFileNameWithoutExtension(_options.ExecutablePath);
            return new UiPathCliResult {
                Success = false,
                Command = $"{_options.ExecutablePath} {arguments}",
                ExitCode = -1,
                Summary = $"UiPath CLI ('{_options.ExecutablePath}') not found.",
                Errors =
                [
                    $"The UiPath CLI ('{_options.ExecutablePath}') was not found on PATH (searched for {baseName}.exe, {baseName}.cmd, {baseName}.bat, {baseName}.ps1).",
                    "Install it (npm install -g @uipath/cli) or set UiPathCli:ExecutablePath in appsettings.json."
                ]
            };
        }

        var command = $"{spec.ResolvedPath} {arguments}";

        var run = await ProcessRunner.RunAsync(
            spec.FileName, spec.ArgumentPrefix + arguments + spec.ArgumentSuffix, workingDirectory,
            TimeSpan.FromSeconds(_options.DefaultTimeoutSeconds), cancellationToken);

        if (run.StartError is not null) {
            // Most common cause: resolved shim or its host (cmd.exe/powershell.exe) failed to start.
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

        if (run.TimedOut) {
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

        var rawLines = new List<string>();
        if (_options.IncludeRawOutput) {
            rawLines.AddRange(ProcessRunner.SplitLines(run.StdOut));
            rawLines.AddRange(ProcessRunner.SplitLines(run.StdErr));
        }

        var (stdout, stderr) = CaptureOutput(run.StdOut, run.StdErr, _options.MaxOutputChars);

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
}
