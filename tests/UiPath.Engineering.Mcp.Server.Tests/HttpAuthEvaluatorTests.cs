using UiPath.Engineering.Mcp.Core.Configuration;

namespace UiPath.Engineering.Mcp.Server.Tests;

public class HttpAuthEvaluatorTests {
    [Fact]
    public void Health_IsAnonymous_SseIsProtected() {
        Assert.True(HttpAuthEvaluator.IsAnonymousPath("/health"));
        Assert.False(HttpAuthEvaluator.IsAnonymousPath("/sse"));
        Assert.True(HttpAuthEvaluator.IsProtectedPath("/sse"));
        Assert.False(HttpAuthEvaluator.IsProtectedPath("/health"));
    }

    [Fact]
    public void IsAuthorized_WhenDisabled_AllowsAnonymous() {
        Assert.True(HttpAuthEvaluator.IsAuthorized(new HttpAuthOptions { Enabled = false, ApiKey = "secret" }, null, null));
    }

    [Fact]
    public void IsAuthorized_WhenEnabledWithoutKey_FailsClosed() {
        Assert.False(HttpAuthEvaluator.IsAuthorized(new HttpAuthOptions { Enabled = true, ApiKey = "" }, "anything", null));
    }

    [Fact]
    public void IsAuthorized_WhenEnabled_AcceptsHeaderOrBearer() {
        var options = new HttpAuthOptions { Enabled = true, ApiKey = "secret" };
        Assert.False(HttpAuthEvaluator.IsAuthorized(options, null, null));
        Assert.True(HttpAuthEvaluator.IsAuthorized(options, "secret", null));
        Assert.True(HttpAuthEvaluator.IsAuthorized(options, null, "Bearer secret"));
        Assert.False(HttpAuthEvaluator.IsAuthorized(options, null, "Bearer wrong"));
    }
}
