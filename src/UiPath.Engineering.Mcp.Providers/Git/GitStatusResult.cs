namespace UiPath.Engineering.Mcp.Providers.Git;

public sealed class GitStatusResult {
    public string RepoPath { get; init; } = string.Empty;
    public string Branch { get; init; } = string.Empty;
    public int AheadCount { get; init; }
    public int BehindCount { get; init; }
    public List<string> ChangedFiles { get; init; } = [];
    public bool IsRepository { get; init; }
    public List<string> Errors { get; init; } = [];
}

public sealed class GitCommitEntry {
    public string Hash { get; init; } = string.Empty;
    public string Author { get; init; } = string.Empty;
    public string Date { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
}

public sealed class GitLogResult {
    public string RepoPath { get; init; } = string.Empty;
    public bool IsRepository { get; init; }
    public List<GitCommitEntry> Commits { get; init; } = [];
    public List<string> Errors { get; init; } = [];
}
