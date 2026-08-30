using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using UiPath.Engineering.Mcp.Core.Configuration;
using UiPath.Engineering.Mcp.Server;

namespace UiPath.Engineering.Mcp.Server.Tests;

public class McpHttpAuthMiddlewareTests {
    [Fact]
    public async Task Health_StaysAnonymousWhenAuthEnabled() {
        var context = CreateContext("/health", "GET");
        var nextCalled = false;
        var middleware = CreateMiddleware(Enabled: true, apiKey: "secret", next: _ => {
            nextCalled = true;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    [Fact]
    public async Task Sse_WhenAuthDisabled_PassesThrough() {
        var context = CreateContext("/sse", "POST");
        var nextCalled = false;
        var middleware = CreateMiddleware(Enabled: false, apiKey: "secret", next: _ => {
            nextCalled = true;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled);
        Assert.NotEqual(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    [Fact]
    public async Task Sse_WhenAuthEnabled_RejectsMissingKey() {
        var context = CreateContext("/sse", "POST");
        var nextCalled = false;
        var middleware = CreateMiddleware(Enabled: true, apiKey: "secret", next: _ => {
            nextCalled = true;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(context);

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    [Fact]
    public async Task Sse_WhenAuthEnabled_AcceptsApiKeyHeader() {
        var context = CreateContext("/sse", "POST");
        context.Request.Headers["X-Api-Key"] = "secret";
        var nextCalled = false;
        var middleware = CreateMiddleware(Enabled: true, apiKey: "secret", next: _ => {
            nextCalled = true;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled);
        Assert.NotEqual(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    [Fact]
    public async Task Sse_WhenAuthEnabled_AcceptsBearerToken() {
        var context = CreateContext("/sse", "POST");
        context.Request.Headers.Authorization = "Bearer secret";
        var nextCalled = false;
        var middleware = CreateMiddleware(Enabled: true, apiKey: "secret", next: _ => {
            nextCalled = true;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled);
        Assert.NotEqual(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    private static DefaultHttpContext CreateContext(string path, string method) {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Request.Method = method;
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static McpHttpAuthMiddleware CreateMiddleware(bool Enabled, string apiKey, RequestDelegate next) =>
        new(next, Options.Create(new McpServerOptions {
            HttpAuth = new HttpAuthOptions { Enabled = Enabled, ApiKey = apiKey }
        }));
}
