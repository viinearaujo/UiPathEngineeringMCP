namespace UiPath.Engineering.Mcp.Core.Configuration;
public sealed class GitLabOptions {
    public string BaseUrl { get; init; } = string.Empty;
    public string ProjectId { get; init; } = string.Empty;
    /// <summary>GitLab personal access token. Never returned by tools, never logged.</summary>
    public string AccessToken { get; init; } = string.Empty;
    public int TimeoutSeconds { get; init; } = 30;
}
