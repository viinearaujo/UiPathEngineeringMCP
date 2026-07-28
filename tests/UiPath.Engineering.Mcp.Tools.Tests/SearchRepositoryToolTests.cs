using System.Text.Json;
using UiPath.Engineering.Mcp.Providers.GitLab;

namespace UiPath.Engineering.Mcp.Tools.Tests;

public class SearchRepositoryToolTests
{
    [Fact]
    public async Task SearchRepository_EmptyQuery_ReturnsError()
    {
        var tool = new SearchRepositoryTool(new FakeGitLabProvider());

        var result = await tool.SearchRepository("");

        Assert.Equal("error", result.Status);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public async Task SearchRepository_GitLabUnconfigured_ReturnsHelpfulError()
    {
        var gitLab = new FakeGitLabProvider
        {
            SearchResult = new GitLabIssueListResult
            {
                Success = false,
                Errors = ["GitLab is not configured. Set GitLab:BaseUrl and GitLab:ProjectId in appsettings.json."]
            }
        };
        var tool = new SearchRepositoryTool(gitLab);

        var result = await tool.SearchRepository("anything");
        var data = JsonSerializer.SerializeToElement(result.Data);

        Assert.Equal("error", result.Status);
        Assert.False(data.GetProperty("success").GetBoolean());
        Assert.Contains("not configured", result.Errors[0]);
    }

    [Fact]
    public async Task SearchRepository_WhenIssuesFound_ReturnsMappedResults()
    {
        var gitLab = new FakeGitLabProvider
        {
            SearchResult = new GitLabIssueListResult
            {
                Success = true,
                Issues =
                [
                    new GitLabIssueSummary { Iid = 3, Title = "Bug", State = "opened", WebUrl = "https://gl/3", Labels = ["bug"], UpdatedAt = "2026-07-01" },
                    new GitLabIssueSummary { Iid = 4, Title = "Story", State = "closed", WebUrl = "https://gl/4" }
                ]
            }
        };
        var tool = new SearchRepositoryTool(gitLab);

        var result = await tool.SearchRepository("bug", 10);
        var data = JsonSerializer.SerializeToElement(result.Data);

        Assert.Equal("success", result.Status);
        Assert.True(data.GetProperty("success").GetBoolean());
        var results = data.GetProperty("results");
        Assert.Equal(2, results.GetArrayLength());
        Assert.Equal(3, results[0].GetProperty("iid").GetInt32());
        Assert.Equal("Bug", results[0].GetProperty("title").GetString());
        Assert.Equal("https://gl/3", results[0].GetProperty("webUrl").GetString());
    }
}
