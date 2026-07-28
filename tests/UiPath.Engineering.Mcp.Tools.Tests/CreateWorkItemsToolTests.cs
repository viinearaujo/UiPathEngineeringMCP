using System.Text.Json;
using UiPath.Engineering.Mcp.Providers.GitLab;

namespace UiPath.Engineering.Mcp.Tools.Tests;

public class CreateWorkItemsToolTests
{
    [Fact]
    public async Task CreateWorkItems_EmptyList_ReturnsError()
    {
        var tool = new CreateWorkItemsTool(new FakeGitLabProvider());

        var result = await tool.CreateWorkItems([]);

        Assert.Equal("error", result.Status);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public async Task CreateWorkItems_AllSucceed_ReturnsCreatedEntries()
    {
        var gitLab = new FakeGitLabProvider();
        var tool = new CreateWorkItemsTool(gitLab);

        var result = await tool.CreateWorkItems(
        [
            new WorkItemInput { Title = "One", Description = "d1" },
            new WorkItemInput { Title = "Two", Labels = ["auto"] }
        ]);
        var data = JsonSerializer.SerializeToElement(result.Data);

        Assert.Equal("success", result.Status);
        Assert.True(data.GetProperty("success").GetBoolean());
        Assert.Equal(2, data.GetProperty("created").GetArrayLength());
        Assert.Equal(0, data.GetProperty("failed").GetArrayLength());
        Assert.Equal(2, gitLab.CreatedIssues.Count);
    }

    [Fact]
    public async Task CreateWorkItems_PartialFailure_ReportsCreatedAndFailed()
    {
        var gitLab = new FakeGitLabProvider
        {
            CreateHandler = (title, _, _) => title == "Bad"
                ? new GitLabIssueResult { Success = false, Errors = ["GitLab request failed with status 500 (InternalServerError)."] }
                : new GitLabIssueResult { Success = true, Issue = new GitLabIssueSummary { Iid = 9, Title = title, WebUrl = "https://gl/9" } }
        };
        var tool = new CreateWorkItemsTool(gitLab);

        var result = await tool.CreateWorkItems(
        [
            new WorkItemInput { Title = "Good" },
            new WorkItemInput { Title = "Bad" }
        ]);
        var data = JsonSerializer.SerializeToElement(result.Data);

        Assert.Equal("error", result.Status);
        Assert.False(data.GetProperty("success").GetBoolean());
        Assert.Single(data.GetProperty("created").EnumerateArray());
        var failed = data.GetProperty("failed");
        Assert.Single(failed.EnumerateArray());
        Assert.Equal("Bad", failed[0].GetProperty("title").GetString());
        Assert.Contains("500", failed[0].GetProperty("error").GetString());
    }

    [Fact]
    public async Task CreateWorkItems_MissingTitle_FailsThatItemWithoutCallingProvider()
    {
        var gitLab = new FakeGitLabProvider();
        var tool = new CreateWorkItemsTool(gitLab);

        var result = await tool.CreateWorkItems([new WorkItemInput { Title = "" }]);
        var data = JsonSerializer.SerializeToElement(result.Data);

        Assert.Equal("error", result.Status);
        Assert.Single(data.GetProperty("failed").EnumerateArray());
        Assert.Empty(gitLab.CreatedIssues);
    }
}
