using UiPath.Engineering.Mcp.Core;
using UiPath.Engineering.Mcp.Core.Abstractions;

namespace UiPath.Engineering.Mcp.Providers.Git;

/// <summary>
/// Wraps the `git` CLI (expected on PATH). All invocations use ProcessStartInfo.ArgumentList
/// with `-C` and the repo path as separate tokens — no caller-controlled shell text.
/// A path that is not a git repository yields IsRepository = false with an
/// explanatory error; this provider never throws for that case.
/// </summary>
public sealed class GitProvider : IGitProvider {
    private const int DefaultTimeoutSeconds = 30;
    private const char FieldSeparator = '\u001f';

    private readonly IFilesystemProvider _filesystem;

    public GitProvider(IFilesystemProvider filesystem) => _filesystem = filesystem;

    public async Task<GitStatusResult> GetStatusAsync(string repoPath, CancellationToken cancellationToken = default) {
        if (!_filesystem.IsPathAllowed(repoPath)) {
            return new GitStatusResult {
                RepoPath = repoPath,
                IsRepository = false,
                Errors = ["Path outside allowed roots."]
            };
        }

        var run = await RunGitAsync(repoPath, ["status", "--porcelain=v1", "--branch"], cancellationToken);

        if (IsNotARepository(run)) {
            return new GitStatusResult {
                RepoPath = repoPath,
                IsRepository = false,
                Errors = [$"'{repoPath}' is not a git repository."]
            };
        }

        if (run.ExitCode != 0) {
            return new GitStatusResult {
                RepoPath = repoPath,
                IsRepository = true,
                Errors = run.Errors
            };
        }

        return GitStatusParser.Parse(repoPath, run.StdOut);
    }

    public async Task<GitLogResult> GetRecentCommitsAsync(string repoPath, int count, CancellationToken cancellationToken = default) {
        if (!_filesystem.IsPathAllowed(repoPath)) {
            return new GitLogResult {
                RepoPath = repoPath,
                IsRepository = false,
                Errors = ["Path outside allowed roots."]
            };
        }

        // Clamp count so it stays a plain validated integer in the argument list.
        var clamped = Math.Clamp(count, 1, 100);
        var format = "--pretty=format:%H%x1f%an%x1f%aI%x1f%s";
        var run = await RunGitAsync(repoPath, ["log", "-n", clamped.ToString(), format], cancellationToken);

        if (IsNotARepository(run)) {
            return new GitLogResult {
                RepoPath = repoPath,
                IsRepository = false,
                Errors = [$"'{repoPath}' is not a git repository."]
            };
        }

        if (run.ExitCode != 0) {
            // An empty repository (no commits yet) exits non-zero; treat as an empty log.
            if (run.StdErr.Contains("does not have any commits", StringComparison.OrdinalIgnoreCase)) {
                return new GitLogResult { RepoPath = repoPath, IsRepository = true };
            }

            return new GitLogResult {
                RepoPath = repoPath,
                IsRepository = true,
                Errors = run.Errors
            };
        }

        var commits = new List<GitCommitEntry>();
        foreach (var line in ProcessRunner.SplitLines(run.StdOut)) {
            var parts = line.Split(FieldSeparator);
            if (parts.Length < 4) {
                continue;
            }

            commits.Add(new GitCommitEntry {
                Hash = parts[0],
                Author = parts[1],
                Date = parts[2],
                Message = parts[3]
            });
        }

        return new GitLogResult {
            RepoPath = repoPath,
            IsRepository = true,
            Commits = commits
        };
    }

    private static bool IsNotARepository(GitRunResult run) =>
        run.ExitCode != 0 && run.StdErr.Contains("not a git repository", StringComparison.OrdinalIgnoreCase);

    internal static List<string> BuildInvocation(string repoPath, params string[] gitArgs) {
        var args = new List<string>(2 + gitArgs.Length) { "-C", repoPath };
        args.AddRange(gitArgs);
        return args;
    }

    private static async Task<GitRunResult> RunGitAsync(string repoPath, string[] gitArgs, CancellationToken cancellationToken) {
        var invocation = BuildInvocation(repoPath, gitArgs);
        var run = await ProcessRunner.RunAsync("git", invocation, null,
            TimeSpan.FromSeconds(DefaultTimeoutSeconds), cancellationToken);

        if (run.StartError is not null) {
            // Most common cause: git not installed / not on PATH.
            return new GitRunResult {
                ExitCode = -1,
                Errors = [$"Could not start 'git': {run.StartError}", "Verify that git is installed and available on PATH."]
            };
        }

        if (run.TimedOut) {
            return new GitRunResult {
                ExitCode = -1,
                Errors = [$"'git {string.Join(" ", gitArgs)}' exceeded the {DefaultTimeoutSeconds}s timeout."]
            };
        }

        var stderr = SecretRedactor.Redact(run.StdErr).Text;
        return new GitRunResult {
            ExitCode = run.ExitCode,
            StdOut = run.StdOut,
            StdErr = stderr,
            Errors = run.ExitCode == 0 ? [] : FormatGitErrors(run.StdErr)
        };
    }

    internal static List<string> FormatGitErrors(string stderr) =>
        [.. SecretRedactor.RedactLines(ProcessRunner.SplitLines(stderr)).Select(l => $"[git] {l}")];

    private sealed class GitRunResult {
        public int ExitCode { get; init; }
        public string StdOut { get; init; } = string.Empty;
        public string StdErr { get; init; } = string.Empty;
        public List<string> Errors { get; init; } = [];
    }
}
