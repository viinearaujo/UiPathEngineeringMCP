using UiPath.Engineering.Mcp.Core.Abstractions;
using UiPath.Engineering.Mcp.Core.Models;
using UiPath.Engineering.Mcp.Core.Parsing;
using UiPath.Engineering.Mcp.Providers.Git;
using UiPath.Engineering.Mcp.Providers.GitLab;
using UiPath.Engineering.Mcp.Providers.UiPathCli;

namespace UiPath.Engineering.Mcp.Tools.Tests;

internal sealed class FakeFilesystemProvider : IFilesystemProvider
{
    public bool Allowed { get; set; } = true;
    public string? ProjectJson { get; set; } = "/projects/testProcess/project.json";

    public bool IsPathAllowed(string requestedPath) => Allowed;
    public string? FindProjectJson(string projectPath) => ProjectJson;
    public IReadOnlyList<string> FindXamlFiles(string projectPath) => [];
    public string ReadAllText(string filePath) => string.Empty;
    public DateTime GetLastWriteTimeUtc(string filePath) => DateTime.UnixEpoch;
    public DirectoryTreeNode GetDirectoryTree(string root, int maxDepth = 3) =>
        new() { Name = Path.GetFileName(root) ?? root, Path = root, IsDirectory = true };
}

internal sealed class FakeProjectModelBuilder : IProjectModelBuilder
{
    public UiPathProjectModel? Model { get; set; }
    public Exception? ToThrow { get; set; }

    public Task<UiPathProjectModel> BuildAsync(string projectPath, CancellationToken cancellationToken = default)
    {
        if (ToThrow is not null)
        {
            return Task.FromException<UiPathProjectModel>(ToThrow);
        }

        return Task.FromResult(Model ?? new UiPathProjectModel { ProjectName = "testProcess" });
    }
}

internal sealed class FakeUiPathCliProvider : IUiPathCliProvider
{
    public UiPathCliResult Result { get; set; } = new() { Success = true, Summary = "Validation completed." };

    public Task<UiPathCliResult> ValidateAsync(
        string projectPath, bool restore, bool analyze, bool pack, CancellationToken cancellationToken = default)
        => Task.FromResult(Result);
}


internal sealed class FakeGitProvider : IGitProvider
{
    public GitStatusResult StatusResult { get; set; } = new() { IsRepository = true, Branch = "main" };
    public GitLogResult LogResult { get; set; } = new() { IsRepository = true };

    public Task<GitStatusResult> GetStatusAsync(string repoPath, CancellationToken cancellationToken = default)
        => Task.FromResult(StatusResult);

    public Task<GitLogResult> GetRecentCommitsAsync(string repoPath, int count, CancellationToken cancellationToken = default)
        => Task.FromResult(LogResult);
}

internal sealed class FakeGitLabProvider : IGitLabProvider
{
    public GitLabIssueListResult SearchResult { get; set; } = new() { Success = true };
    public Func<string, string, IReadOnlyList<string>, GitLabIssueResult>? CreateHandler { get; set; }
    public List<(string Title, string Description)> CreatedIssues { get; } = [];

    public Task<GitLabIssueListResult> SearchIssuesAsync(string query, int maxResults, CancellationToken cancellationToken = default)
        => Task.FromResult(SearchResult);

    public Task<GitLabIssueResult> CreateIssueAsync(string title, string description, IReadOnlyList<string> labels, CancellationToken cancellationToken = default)
    {
        CreatedIssues.Add((title, description));
        var result = CreateHandler?.Invoke(title, description, labels)
            ?? new GitLabIssueResult
            {
                Success = true,
                Issue = new GitLabIssueSummary { Iid = CreatedIssues.Count, Title = title, WebUrl = $"https://gitlab.example.com/p/-/issues/{CreatedIssues.Count}" }
            };
        return Task.FromResult(result);
    }
}
