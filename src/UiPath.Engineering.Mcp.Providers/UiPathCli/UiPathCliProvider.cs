using Microsoft.Extensions.Options;
using UiPath.Engineering.Mcp.Core.Configuration;

namespace UiPath.Engineering.Mcp.Providers.UiPathCli;

public sealed class UiPathCliProvider : IUiPathCliProvider {
    private readonly UiPathCliOptions _options;

    public UiPathCliProvider(IOptions<UiPathCliOptions> options) => _options = options.Value;

    public async Task<UiPathCliResult> ValidateAsync(
        string projectPath,
        bool restore,
        bool analyze,
        bool pack,
        CancellationToken cancellationToken = default) {
        // uip.exe accepts exactly ONE verb per invocation. Running "restore analyze pack"
        // as a single command is invalid, so each requested step is executed sequentially
        // and the results are aggregated into a single structured response.
        var errors = new List<string>();
        var warnings = new List<string>();
        var rawOutput = new List<string>();
        var executedCommands = new List<string>();
        var overallSuccess = true;
        var lastExitCode = 0;

        var steps = new List<(string Verb, bool Enabled)>
        {
            ("restore", restore),
            ("analyze", analyze),
            ("pack", pack)
        };

        // Steps that are not requested, or skipped after an earlier failure, keep
        // Executed = false so callers can distinguish "not run" from "ran clean".
        var stepResults = new Dictionary<string, CliStepResult> {
            ["restore"] = new CliStepResult(),
            ["analyze"] = new CliStepResult(),
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
                Warnings = stepResult.Warnings
            };

            executedCommands.Add(stepResult.Command);
            errors.AddRange(stepResult.Errors);
            warnings.AddRange(stepResult.Warnings);
            rawOutput.AddRange(stepResult.RawOutputLines);
            lastExitCode = stepResult.ExitCode;

            if (!stepResult.Success) {
                overallSuccess = false;
                // Stop the pipeline on the first failing step (e.g. a failed restore
                // should not be followed by analyze/pack against a broken state).
                break;
            }
        }

        return new UiPathCliResult {
            Success = overallSuccess,
            Command = string.Join(" && ", executedCommands),
            ExitCode = lastExitCode,
            Summary = overallSuccess ? "Validation completed." : "Validation failed.",
            Restore = stepResults["restore"],
            Analyze = stepResults["analyze"],
            Pack = stepResults["pack"],
            Errors = errors,
            Warnings = warnings,
            RawOutputLines = _options.IncludeRawOutput ? rawOutput : []
        };
    }

    // Normalize input: uip.exe can take either a project directory or an explicit path
    // to project.json. We pass the path explicitly and quote it so paths with spaces
    // (e.g. OneDrive folders) work.
    private string BuildVerbArguments(string verb, string projectPath) {
        var arguments = $"{verb} \"{projectPath}\"";

        if (verb == "pack" && !string.IsNullOrWhiteSpace(_options.DefaultPackOutputDirectory)) {
            arguments += $" --output \"{_options.DefaultPackOutputDirectory}\"";
        }

        return arguments;
    }

    public async Task<UiPathCliResult> RunAsync(
        string verb,
        string arguments,
        string? workingDirectory = null,
        CancellationToken cancellationToken = default) {
        var command = $"{_options.ExecutablePath} {arguments}";

        var run = await ProcessRunner.RunAsync(
            _options.ExecutablePath, arguments, workingDirectory,
            TimeSpan.FromSeconds(_options.DefaultTimeoutSeconds), cancellationToken);

        if (run.StartError is not null) {
            // Most common cause: uip.exe not found on PATH / wrong ExecutablePath.
            return new UiPathCliResult {
                Success = false,
                Command = command,
                ExitCode = -1,
                Summary = $"Failed to start '{_options.ExecutablePath}'.",
                Errors =
                [
                    $"Could not start the UiPath CLI ('{_options.ExecutablePath}'): {run.StartError}",
                    "Verify that uip.exe is installed and available on PATH, or set UiPathCli:ExecutablePath in appsettings.json."
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

        var (errors, warnings) = UiPathCliOutputParser.Parse(verb, run.StdOut, run.StdErr);

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

        return new UiPathCliResult {
            Success = run.ExitCode == 0,
            Command = command,
            ExitCode = run.ExitCode,
            Summary = run.ExitCode == 0 ? $"'{verb}' completed." : $"'{verb}' failed.",
            Errors = errors,
            Warnings = warnings,
            RawOutputLines = rawLines
        };
    }
}
