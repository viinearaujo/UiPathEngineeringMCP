using System.Diagnostics;
using UiPath.Engineering.Mcp.Core.Abstractions;

namespace UiPath.Engineering.Mcp.Providers.Git;

/// <summary>
/// Wraps the `git` CLI (expected on PATH). All invocations use fixed argument
/// templates with validated inputs only — no caller-controlled shell text.
/// A path that is not a git repository yields IsRepository = false with an
/// explanatory error; this provider never throws for that case.
/// </summary>
public sealed class GitProvider : IGitProvider
{
    private const int DefaultTimeoutSeconds = 30;
    private const char FieldSeparator = '\u001f';

    private readonly IFilesystemProvider _filesystem;

    public GitProvider(IFilesystemProvider filesystem) => _filesystem = filesystem;

    public async Task<GitStatusResult> GetStatusAsync(string repoPath, CancellationToken cancellationToken = default)
    {
        if (!_filesystem.IsPathAllowed(repoPath))
        {
            return new GitStatusResult
            {
                RepoPath = repoPath,
                IsRepository = false,
                Errors = ["Path outside allowed roots."]
            };
        }

        var run = await RunGitAsync(repoPath, "status --porcelain=v1 --branch", cancellationToken);

        if (IsNotARepository(run))
        {
            return new GitStatusResult
            {
                RepoPath = repoPath,
                IsRepository = false,
                Errors = [$"'{repoPath}' is not a git repository."]
            };
        }

        if (run.ExitCode != 0)
        {
            return new GitStatusResult
            {
                RepoPath = repoPath,
                IsRepository = true,
                Errors = run.Errors
            };
        }

        return GitStatusParser.Parse(repoPath, run.StdOut);
    }

    public async Task<GitLogResult> GetRecentCommitsAsync(string repoPath, int count, CancellationToken cancellationToken = default)
    {
        if (!_filesystem.IsPathAllowed(repoPath))
        {
            return new GitLogResult
            {
                RepoPath = repoPath,
                IsRepository = false,
                Errors = ["Path outside allowed roots."]
            };
        }

        // Clamp count so it stays a plain validated integer in the fixed template.
        var clamped = Math.Clamp(count, 1, 100);
        var format = "--pretty=format:%H%x1f%an%x1f%aI%x1f%s";
        var run = await RunGitAsync(repoPath, $"log -n {clamped} {format}", cancellationToken);

        if (IsNotARepository(run))
        {
            return new GitLogResult
            {
                RepoPath = repoPath,
                IsRepository = false,
                Errors = [$"'{repoPath}' is not a git repository."]
            };
        }

        if (run.ExitCode != 0)
        {
            // An empty repository (no commits yet) exits non-zero; treat as an empty log.
            if (run.StdErr.Contains("does not have any commits", StringComparison.OrdinalIgnoreCase))
            {
                return new GitLogResult { RepoPath = repoPath, IsRepository = true };
            }

            return new GitLogResult
            {
                RepoPath = repoPath,
                IsRepository = true,
                Errors = run.Errors
            };
        }

        var commits = new List<GitCommitEntry>();
        foreach (var line in SplitLines(run.StdOut))
        {
            var parts = line.Split(FieldSeparator);
            if (parts.Length < 4)
            {
                continue;
            }

            commits.Add(new GitCommitEntry
            {
                Hash = parts[0],
                Author = parts[1],
                Date = parts[2],
                Message = parts[3]
            });
        }

        return new GitLogResult
        {
            RepoPath = repoPath,
            IsRepository = true,
            Commits = commits
        };
    }

    private static bool IsNotARepository(GitRunResult run) =>
        run.ExitCode != 0 && run.StdErr.Contains("not a git repository", StringComparison.OrdinalIgnoreCase);

    private static async Task<GitRunResult> RunGitAsync(string repoPath, string arguments, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = $"-C \"{repoPath}\" {arguments}",
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
            // Most common cause: git not installed / not on PATH.
            return new GitRunResult
            {
                ExitCode = -1,
                Errors = [$"Could not start 'git': {ex.Message}", "Verify that git is installed and available on PATH."]
            };
        }

        if (process is null)
        {
            return new GitRunResult { ExitCode = -1, Errors = ["Process start returned null."] };
        }

        using (process)
        using (var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            cts.CancelAfter(TimeSpan.FromSeconds(DefaultTimeoutSeconds));

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
                return new GitRunResult
                {
                    ExitCode = -1,
                    Errors = [$"'git {arguments}' exceeded the {DefaultTimeoutSeconds}s timeout."]
                };
            }

            var stdOut = await stdOutTask;
            var stdErr = await stdErrTask;

            return new GitRunResult
            {
                ExitCode = process.ExitCode,
                StdOut = stdOut,
                StdErr = stdErr,
                Errors = process.ExitCode == 0
                    ? []
                    : [.. SplitLines(stdErr).Select(l => $"[git] {l}")]
            };
        }
    }

    private static IEnumerable<string> SplitLines(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? []
            : text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

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

    private sealed class GitRunResult
    {
        public int ExitCode { get; init; }
        public string StdOut { get; init; } = string.Empty;
        public string StdErr { get; init; } = string.Empty;
        public List<string> Errors { get; init; } = [];
    }
}
