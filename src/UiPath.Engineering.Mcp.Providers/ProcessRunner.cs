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

    /// <summary>Hard cap on captured stdout+stderr so a noisy CLI cannot exhaust memory.</summary>
    internal const int MaxCapturedChars = 4_000_000;

    public static async Task<ProcessRunResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? environment = null,
        int? maxCapturedChars = null) {

        var psi = CreateStartInfo(fileName, arguments, workingDirectory, environment);

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

        var budget = new CaptureBudget(maxCapturedChars is > 0 ? maxCapturedChars.Value : MaxCapturedChars);
        var stdout = new CapturedText();
        var stderr = new CapturedText();

        using (process)
        using (var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        using (var readCts = new CancellationTokenSource()) {
            cts.CancelAfter(timeout);

            var stdOutTask = ReadStreamAsync(process.StandardOutput, stdout, budget, readCts.Token);
            var stdErrTask = ReadStreamAsync(process.StandardError, stderr, budget, readCts.Token);

            try {
                await process.WaitForExitAsync(cts.Token);
            } catch (OperationCanceledException) {
                TryKill(process);
                var drained = await WaitForReadsAsync(stdOutTask, stdErrTask, readCts);
                var canceled = cancellationToken.IsCancellationRequested;
                return new ProcessRunResult {
                    ExitCode = -1,
                    TimedOut = !canceled,
                    Canceled = canceled,
                    StdOut = stdout.Text(),
                    StdErr = stderr.Text(),
                    OutputTruncated = !drained || budget.Truncated
                };
            }

            var finished = await WaitForReadsAsync(stdOutTask, stdErrTask, readCts);
            return new ProcessRunResult {
                ExitCode = process.ExitCode,
                StdOut = stdout.Text(),
                StdErr = stderr.Text(),
                OutputTruncated = !finished || budget.Truncated
            };
        }
    }

    internal static ProcessStartInfo CreateStartInfo(
        string fileName,
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        IReadOnlyDictionary<string, string>? environment = null) {
        var psi = new ProcessStartInfo {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = workingDirectory ?? string.Empty
        };
        if (environment is not null) {
            foreach (var (key, value) in environment) {
                psi.Environment[key] = value;
            }
        }
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

    internal static async Task<bool> WaitForReadsAsync(
        Task stdOutTask,
        Task stdErrTask,
        CancellationTokenSource readCancellation) {
        if (await ReadsFinished(stdOutTask, stdErrTask)) {
            return true;
        }

        // The pipes were still open. Cancel the reads so dispose does not fault them,
        // and keep the truncated flag even if that cancel lets the tasks finish.
        readCancellation.Cancel();
        await ReadsFinished(stdOutTask, stdErrTask);
        return false;
    }

    private static async Task<bool> ReadsFinished(Task stdOutTask, Task stdErrTask) {
        try {
            await Task.WhenAll(stdOutTask, stdErrTask).WaitAsync(OutputDrainTimeout);
            return true;
        } catch (TimeoutException) {
            return false;
        } catch (Exception) {
            return stdOutTask.IsCompleted && stdErrTask.IsCompleted;
        }
    }

    private static async Task ReadStreamAsync(
        StreamReader reader,
        CapturedText captured,
        CaptureBudget budget,
        CancellationToken cancellationToken) {
        var chunk = new char[8192];
        try {
            while (true) {
                var read = await reader.ReadAsync(chunk.AsMemory(), cancellationToken);
                if (read == 0) {
                    return;
                }

                var keep = budget.Accept(read);
                if (keep > 0) {
                    captured.Append(chunk, keep);
                }
            }
        } catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException) {
            // The drain canceled the read, or dispose closed the pipe.
        }
    }

    private sealed class CapturedText {
        private readonly StringBuilder _builder = new();
        private readonly object _gate = new();

        public void Append(char[] buffer, int count) {
            lock (_gate) {
                _builder.Append(buffer, 0, count);
            }
        }

        public string Text() {
            lock (_gate) {
                return _builder.ToString();
            }
        }
    }

    private sealed class CaptureBudget {
        private int _remaining;
        private int _truncated;

        public CaptureBudget(int max) => _remaining = max;

        public bool Truncated => Volatile.Read(ref _truncated) == 1;

        public int Accept(int count) {
            while (true) {
                var current = Volatile.Read(ref _remaining);
                if (current <= 0) {
                    MarkTruncated();
                    return 0;
                }

                var take = Math.Min(current, count);
                if (Interlocked.CompareExchange(ref _remaining, current - take, current) == current) {
                    if (take < count) {
                        MarkTruncated();
                    }

                    return take;
                }
            }
        }

        private void MarkTruncated() => Interlocked.Exchange(ref _truncated, 1);
    }

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
    public bool OutputTruncated { get; init; }
    public string StdOut { get; init; } = string.Empty;
    public string StdErr { get; init; } = string.Empty;
}
