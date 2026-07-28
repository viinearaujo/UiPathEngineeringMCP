using System.Net;
using System.Text;
using Microsoft.Extensions.Options;
using UiPath.Engineering.Mcp.Core.Configuration;
using UiPath.Engineering.Mcp.Providers.GitLab;

namespace UiPath.Engineering.Mcp.Providers.Tests;

public class GitLabProviderTests {
    private const string Token = "super-secret-token-123";

    private static GitLabProvider CreateSut(HttpMessageHandler handler, GitLabOptions? options = null) {
        var opts = options ?? new GitLabOptions {
            BaseUrl = "https://gitlab.example.com",
            ProjectId = "42",
            AccessToken = Token,
            TimeoutSeconds = 30
        };
        return new GitLabProvider(new HttpClient(handler), Options.Create(opts));
    }

    private sealed class FakeHttpMessageHandler : HttpMessageHandler {
        public HttpStatusCode StatusCode { get; set; } = HttpStatusCode.OK;
        public string ResponseBody { get; set; } = "[]";
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
            LastRequest = request;
            LastRequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);

            return new HttpResponseMessage(StatusCode) {
                Content = new StringContent(ResponseBody, Encoding.UTF8, "application/json"),
                RequestMessage = request
            };
        }
    }

    [Fact]
    public async Task SearchIssuesAsync_MapsIssueFields() {
        var handler = new FakeHttpMessageHandler {
            ResponseBody = """
                [{"iid":7,"title":"Fix reconciliation","state":"opened","web_url":"https://gitlab.example.com/p/-/issues/7","labels":["bug","finance"],"updated_at":"2026-07-01T10:00:00.000Z"}]
                """
        };
        var sut = CreateSut(handler);

        var result = await sut.SearchIssuesAsync("reconciliation", 10);

        Assert.True(result.Success);
        var issue = Assert.Single(result.Issues);
        Assert.Equal(7, issue.Iid);
        Assert.Equal("Fix reconciliation", issue.Title);
        Assert.Equal("opened", issue.State);
        Assert.Equal("https://gitlab.example.com/p/-/issues/7", issue.WebUrl);
        Assert.Equal(["bug", "finance"], issue.Labels);
        Assert.Equal("2026-07-01T10:00:00.000Z", issue.UpdatedAt);
    }

    [Fact]
    public async Task SearchIssuesAsync_SendsTokenHeaderAndSearchQuery() {
        var handler = new FakeHttpMessageHandler();
        var sut = CreateSut(handler);

        await sut.SearchIssuesAsync("hello world", 5);

        Assert.NotNull(handler.LastRequest);
        Assert.Equal(Token, handler.LastRequest!.Headers.GetValues("PRIVATE-TOKEN").Single());
        var uri = handler.LastRequest!.RequestUri!;
        Assert.Contains("/api/v4/projects/42/issues", uri.ToString());
        Assert.Contains("search=hello%20world", uri.OriginalString);
        Assert.Contains("per_page=5", uri.OriginalString);
    }

    [Fact]
    public async Task CreateIssueAsync_PostsCorrectPayload() {
        var handler = new FakeHttpMessageHandler {
            StatusCode = HttpStatusCode.Created,
            ResponseBody = """{"iid":11,"title":"New item","state":"opened","web_url":"https://gitlab.example.com/p/-/issues/11","labels":["auto"],"updated_at":"2026-07-28T00:00:00.000Z"}"""
        };
        var sut = CreateSut(handler);

        var result = await sut.CreateIssueAsync("New item", "Body text", ["auto"]);

        Assert.True(result.Success);
        Assert.NotNull(result.Issue);
        Assert.Equal(11, result.Issue!.Iid);
        Assert.Equal("https://gitlab.example.com/p/-/issues/11", result.Issue.WebUrl);

        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Contains("\"title\":\"New item\"", handler.LastRequestBody);
        Assert.Contains("\"description\":\"Body text\"", handler.LastRequestBody);
        Assert.Contains("\"labels\":\"auto\"", handler.LastRequestBody);
    }

    [Fact]
    public async Task SearchIssuesAsync_NonSuccessStatus_ReturnsSanitizedErrorWithoutToken() {
        var handler = new FakeHttpMessageHandler {
            StatusCode = HttpStatusCode.Unauthorized,
            ResponseBody = """{"message":"401 Unauthorized"}"""
        };
        var sut = CreateSut(handler);

        var result = await sut.SearchIssuesAsync("x", 10);

        Assert.False(result.Success);
        var error = Assert.Single(result.Errors);
        Assert.Contains("401", error);
        Assert.DoesNotContain(Token, error);
    }

    [Fact]
    public async Task CreateIssueAsync_ServerError_DoesNotLeakTokenOrBody() {
        var handler = new FakeHttpMessageHandler {
            StatusCode = HttpStatusCode.InternalServerError,
            ResponseBody = "stack trace with token super-secret-token-123 inside"
        };
        var sut = CreateSut(handler);

        var result = await sut.CreateIssueAsync("t", "d", []);

        Assert.False(result.Success);
        Assert.All(result.Errors, e => Assert.DoesNotContain(Token, e));
    }

    [Fact]
    public async Task SearchIssuesAsync_NotConfigured_FailsFastWithoutHttpCall() {
        var handler = new FakeHttpMessageHandler();
        var sut = CreateSut(handler, new GitLabOptions { BaseUrl = "", ProjectId = "" });

        var result = await sut.SearchIssuesAsync("x", 10);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Contains("not configured", StringComparison.OrdinalIgnoreCase));
        Assert.Null(handler.LastRequest);
        Assert.All(result.Errors, e => Assert.DoesNotContain(Token, e));
    }
}
