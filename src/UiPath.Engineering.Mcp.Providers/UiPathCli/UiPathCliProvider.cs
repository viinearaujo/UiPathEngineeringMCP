using System.Diagnostics;
using Microsoft.Extensions.Options;
using UiPath.Engineering.Mcp.Core.Configuration;

namespace UiPath.Engineering.Mcp.Providers.UiPathCli;

public sealed class UiPathCliProvider : IUiPathCliProvider
{
    private readonly UiPathCliOptions _options;

    public UiPathCliProvider(IOptions<UiPathCliOptions> options) => _options = options.Value;

    public async Task<UiPathCliResult> ValidateAsync(
        string projectPath,
        bool restore,
        bool analyze,
        bool pack,
        CancellationToken cancellationToken = default)
    {
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

        foreach (var (verb, enabled) in steps)
        {
            if (!enabled)
            {
                continue;
            }

            var stepResult = await RunVerbAsync(verb, projectPath, cancellationToken);

            executedCommands.Add(stepResult.Command);
            errors.AddRange(stepResult.Errors);
            warnings.AddRange(stepResult.Warnings);
            rawOutput.AddRange(stepResult.RawOutputLines);
            lastExitCode = stepResult.ExitCode;

            if (!stepResult.Success)
            {
                overallSuccess = false;
                // Stop the pipeline on the first failing step (e.g. a failed restore
                // should not be followed by analyze/pack against a broken state).
                break;
            }
        }

        return new UiPathCliResult
        {
            Success = overallSuccess,
            Command = string.Join(" && ", executedCommands),
            ExitCode = lastExitCode,
            Summary = overallSuccess ? "Validation completed." : "Validation failed.",
            Errors = errors,
            Warnings = warnings,
            RawOutputLines = _options.IncludeRawOutput ? rawOutput : []
        };
    }

    private async Task<UiPathCliResult> RunVerbAsync(
        string verb,
        string projectPath,
        CancellationToken cancellationToken)
    {
        // Normalize input: uip.exe can take either a project directory or an explicit path
        // to project.json. We pass the path explicitly and quote it so paths with spaces
        // (e.g. OneDrive folders) work.
        var arguments = $"{verb} \"{projectPath}\"";

        if (verb == "pack" && !string.IsNullOrWhiteSpace(_options.DefaultPackOutputDirectory))
        {
            arguments += $" --output \"{_options.DefaultPackOutputDirectory}\"";
        }

        var command = $"{_options.ExecutablePath} {arguments}";

        var psi = new ProcessStartInfo
        {
            FileName = _options.ExecutablePath,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        Process? process;
        try
        {
            process = Process.Start(psi);
        }
        catch (Exception ex)
        {
            // Most common cause: uip.exe not found on PATH / wrong ExecutablePath.
            return new UiPathCliResult
            {
                Success = false,
                Command = command,
                ExitCode = -1,
                Summary = $"Failed to start '{_options.ExecutablePath}'.",
                Errors =
                [
                    $"Could not start the UiPath CLI ('{_options.ExecutablePath}'): {ex.Message}",
                    "Verify that uip.exe is installed and available on PATH, or set UiPathCli:ExecutablePath in appsettings.json."
                ]
            };
        }

        if (process is null)
        {
            return new UiPathCliResult
            {
                Success = false,
                Command = command,
                ExitCode = -1,
                Summary = $"Failed to start '{_options.ExecutablePath}'.",
                Errors = ["Process start returned null."]
            };
        }

        using (process)
        using (var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            cts.CancelAfter(TimeSpan.FromSeconds(_options.DefaultTimeoutSeconds));

            // Read streams concurrently with the wait to avoid deadlocks on large output.
            var stdOutTask = process.StandardOutput.ReadToEndAsync();
            var stdErrTask = process.StandardError.ReadToEndAsync();

            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                return new UiPathCliResult
                {
                    Success = false,
                    Command = command,
                    ExitCode = -1,
                    Summary = $"CLI '{verb}' execution timed out.",
                    Errors = [$"'{verb}' exceeded the {_options.DefaultTimeoutSeconds}s timeout."]
                };
            }

            var stdOut = await stdOutTask;
            var stdErr = await stdErrTask;

            var errors = new List<string>();
            var warnings = new List<string>();

            if (!string.IsNullOrWhiteSpace(stdErr))
            {
                errors.Add($"[{verb}] {stdErr.Trim()}");
            }

            if (stdOut.Contains("error", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"[{verb}] CLI reported errors in output.");
            }

            if (stdOut.Contains("warning", StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add($"[{verb}] CLI reported warnings in output.");
            }

            var rawLines = new List<string>();
            if (_options.IncludeRawOutput)
            {
                rawLines.AddRange(SplitLines(stdOut));
                rawLines.AddRange(SplitLines(stdErr));
            }

            return new UiPathCliResult
            {
                Success = process.ExitCode == 0,
                Command = command,
                ExitCode = process.ExitCode,
                Summary = process.ExitCode == 0 ? $"'{verb}' completed." : $"'{verb}' failed.",
                Errors = errors,
                Warnings = warnings,
                RawOutputLines = rawLines
            };
        }
    }

    private static IEnumerable<string> SplitLines(string text) =>
        string.IsNullOrEmpty(text)
            ? []
            : text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best-effort cleanup; nothing actionable if the kill fails.
        }
    }
}
