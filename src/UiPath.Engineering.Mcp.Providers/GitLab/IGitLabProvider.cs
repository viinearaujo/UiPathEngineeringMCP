namespace UiPath.Engineering.Mcp.Providers.GitLab;

public interface IGitLabProvider
{
    Task<GitLabIssueListResult> SearchIssuesAsync(string query, int maxResults, CancellationToken cancellationToken = default);
    Task<GitLabIssueResult> CreateIssueAsync(string title, string description, IReadOnlyList<string> labels, CancellationToken cancellationToken = default);
}
