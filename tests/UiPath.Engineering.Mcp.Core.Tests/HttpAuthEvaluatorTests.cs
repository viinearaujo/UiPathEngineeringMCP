using UiPath.Engineering.Mcp.Core.Configuration;

namespace UiPath.Engineering.Mcp.Core.Tests;

public class HttpAuthEvaluatorTests {
    [Fact]
    public void Health_IsAnonymous_SseIsProtected() {
        Assert.True(HttpAuthEvaluator.IsAnonymousPath("/health"));
        Assert.True(HttpAuthEvaluator.IsAnonymousPath("/health/ready"));
        Assert.False(HttpAuthEvaluator.IsAnonymousPath("/sse"));
        Assert.True(HttpAuthEvaluator.IsProtectedPath("/sse"));
        Assert.True(HttpAuthEvaluator.IsProtectedPath("/sse/message"));
        Assert.False(HttpAuthEvaluator.IsProtectedPath("/health"));
    }

    [Fact]
    public void IsAuthorized_WhenDisabled_AllowsAnonymous() {
        var options = new HttpAuthOptions { Enabled = false, ApiKey = "secret" };
        Assert.True(HttpAuthEvaluator.IsAuthorized(options, headerValue: null, authorization: null));
    }

    [Fact]
    public void IsAuthorized_WhenEnabledWithoutKey_FailsClosed() {
        var options = new HttpAuthOptions { Enabled = true, ApiKey = "" };
        Assert.False(HttpAuthEvaluator.IsAuthorized(options, "anything", null));
    }

    [Fact]
    public void IsAuthorized_WhenEnabled_AcceptsHeaderOrBearer() {
        var options = new HttpAuthOptions { Enabled = true, ApiKey = "secret" };
        Assert.False(HttpAuthEvaluator.IsAuthorized(options, null, null));
        Assert.False(HttpAuthEvaluator.IsAuthorized(options, "wrong", null));
        Assert.True(HttpAuthEvaluator.IsAuthorized(options, "secret", null));
        Assert.True(HttpAuthEvaluator.IsAuthorized(options, null, "Bearer secret"));
        Assert.False(HttpAuthEvaluator.IsAuthorized(options, null, "Bearer wrong"));
    }
}
