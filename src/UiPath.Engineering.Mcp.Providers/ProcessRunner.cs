using System.Diagnostics;

namespace UiPath.Engineering.Mcp.Providers;

// Shared external-process plumbing: fixed ProcessStartInfo (no shell), concurrent
// stdout/stderr reads to avoid deadlocks on large output, timeout via a linked
// cancellation token, and best-effort process-tree kill on timeout. Never throws
// for start/timeout failures; those come back on the result.
internal static class ProcessRunner {
    public static async Task<ProcessRunResult> RunAsync(
        string fileName,
        string arguments,
        string? workingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken) {

        var psi = new ProcessStartInfo {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory ?? string.Empty
        };

        Process? process;
        try {
            process = Process.Start(psi);
        } catch (Exception ex) {
            // Most common cause: executable not installed / not on PATH.
            return new ProcessRunResult { ExitCode = -1, StartError = ex.Message };
        }

        if (process is null) {
            return new ProcessRunResult { ExitCode = -1, StartError = "Process start returned null." };
        }

        using (process)
        using (var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)) {
            cts.CancelAfter(timeout);

            var stdOutTask = process.StandardOutput.ReadToEndAsync();
            var stdErrTask = process.StandardError.ReadToEndAsync();

            try {
                await process.WaitForExitAsync(cts.Token);
            } catch (OperationCanceledException) {
                TryKill(process);
                return new ProcessRunResult { ExitCode = -1, TimedOut = true };
            }

            return new ProcessRunResult {
                ExitCode = process.ExitCode,
                StdOut = await stdOutTask,
                StdErr = await stdErrTask
            };
        }
    }

    public static List<string> SplitLines(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? []
            : text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).ToList();

    private static void TryKill(Process process) {
        try {
            if (!process.HasExited) {
                process.Kill(entireProcessTree: true);
            }
        } catch {
            // Best-effort cleanup; nothing actionable if the kill fails.
        }
    }
}

internal sealed class ProcessRunResult {
    public int ExitCode { get; init; }
    public string? StartError { get; init; }
    public bool TimedOut { get; init; }
    public string StdOut { get; init; } = string.Empty;
    public string StdErr { get; init; } = string.Empty;
}
