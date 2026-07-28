using System.Text.Json.Serialization;

namespace UiPath.Engineering.Mcp.Providers.GitLab;

public sealed class GitLabIssueSummary {
    [JsonPropertyName("iid")]
    public int Iid { get; init; }

    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    [JsonPropertyName("state")]
    public string State { get; init; } = string.Empty;

    [JsonPropertyName("web_url")]
    public string WebUrl { get; init; } = string.Empty;

    [JsonPropertyName("labels")]
    public List<string> Labels { get; init; } = [];

    [JsonPropertyName("updated_at")]
    public string UpdatedAt { get; init; } = string.Empty;
}

public sealed class GitLabIssueListResult {
    public bool Success { get; init; }
    public List<GitLabIssueSummary> Issues { get; init; } = [];
    public List<string> Errors { get; init; } = [];
}

public sealed class GitLabIssueResult {
    public bool Success { get; init; }
    public GitLabIssueSummary? Issue { get; init; }
    public List<string> Errors { get; init; } = [];
}
