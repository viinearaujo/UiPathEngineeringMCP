namespace UiPath.Engineering.Mcp.Providers.Git;

public interface IGitProvider
{
    Task<GitStatusResult> GetStatusAsync(string repoPath, CancellationToken cancellationToken = default);
    Task<GitLogResult> GetRecentCommitsAsync(string repoPath, int count, CancellationToken cancellationToken = default);
}
