using System.Diagnostics;
using System.Text;

namespace UiPath.Engineering.Mcp.Providers;

// Shared external-process plumbing: ArgumentList only (never a concatenated
// command string), concurrent stdout/stderr reads to avoid deadlocks on large
// output, timeout via a linked cancellation token, and best-effort process-tree
// kill on timeout or caller cancel. Never throws for start/timeout/cancel
// failures; those come back on the result. Caller cancellation and timeout are
// distinct flags so a canceled MCP request is not reported as a 300s CLI timeout.
internal static class ProcessRunner {
    internal static readonly TimeSpan OutputDrainTimeout = TimeSpan.FromSeconds(2);

    public static async Task<ProcessRunResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken) {

        var psi = CreateStartInfo(fileName, arguments, workingDirectory);

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
                var (stdOut, stdErr) = await DrainOutputAsync(stdOutTask, stdErrTask);
                var canceled = cancellationToken.IsCancellationRequested;
                return new ProcessRunResult {
                    ExitCode = -1,
                    TimedOut = !canceled,
                    Canceled = canceled,
                    StdOut = stdOut,
                    StdErr = stdErr
                };
            }

            return new ProcessRunResult {
                ExitCode = process.ExitCode,
                StdOut = await stdOutTask,
                StdErr = await stdErrTask
            };
        }
    }

    internal static ProcessStartInfo CreateStartInfo(
        string fileName,
        IReadOnlyList<string> arguments,
        string? workingDirectory) {
        var psi = new ProcessStartInfo {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory ?? string.Empty
        };
        foreach (var argument in arguments) {
            psi.ArgumentList.Add(argument);
        }
        return psi;
    }

    // Splits a caller-facing argument string into ArgumentList tokens. Double
    // quotes group a token (and are not themselves part of the token); they are
    // not a shell. Unquoted whitespace is the separator.
    internal static List<string> SplitQuotedArguments(string arguments) {
        var tokens = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        foreach (var c in arguments) {
            if (c == '"') {
                inQuotes = !inQuotes;
                continue;
            }

            if (char.IsWhiteSpace(c) && !inQuotes) {
                if (current.Length > 0) {
                    tokens.Add(current.ToString());
                    current.Clear();
                }
                continue;
            }

            current.Append(c);
        }

        if (current.Length > 0) {
            tokens.Add(current.ToString());
        }

        return tokens;
    }

    public static List<string> SplitLines(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? []
            : text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).ToList();

    internal static async Task<(string StdOut, string StdErr)> DrainOutputAsync(
        Task<string> stdOutTask,
        Task<string> stdErrTask) {
        try {
            await Task.WhenAll(stdOutTask, stdErrTask).WaitAsync(OutputDrainTimeout);
            return (await stdOutTask, await stdErrTask);
        } catch (TimeoutException) {
            return (CompletedOrEmpty(stdOutTask), CompletedOrEmpty(stdErrTask));
        } catch {
            return (CompletedOrEmpty(stdOutTask), CompletedOrEmpty(stdErrTask));
        }
    }

    private static string CompletedOrEmpty(Task<string> task) =>
        task.IsCompletedSuccessfully ? task.Result : string.Empty;

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
    public bool Canceled { get; init; }
    public string StdOut { get; init; } = string.Empty;
    public string StdErr { get; init; } = string.Empty;
}
