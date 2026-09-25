using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
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
    public async Task HealthReady_StaysAnonymousWhenAuthEnabled() {
        var context = CreateContext("/health/ready", "GET");
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

    [Fact]
    public async Task Sse_WhenAuthEnabled_AcceptsPreviousApiKey() {
        var context = CreateContext("/sse", "POST");
        context.Request.Headers["X-Api-Key"] = "rotated";
        var nextCalled = false;
        var middleware = CreateMiddleware(
            Enabled: true,
            apiKey: "current",
            next: _ => {
                nextCalled = true;
                return Task.CompletedTask;
            },
            previousApiKey: "rotated");

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled);
        Assert.NotEqual(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    [Fact]
    public async Task Sse_WhenEntraSucceeds_AllowsWithoutApiKey() {
        var context = CreateContext("/sse", "POST", new StubEntraAuthenticator(succeed: true));
        var nextCalled = false;
        var middleware = CreateMiddleware(
            Enabled: true,
            apiKey: "secret",
            next: _ => {
                nextCalled = true;
                return Task.CompletedTask;
            },
            entraEnabled: true);

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled);
        Assert.NotEqual(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    [Fact]
    public async Task Sse_WhenEntraFails_FallsThroughToApiKeyBearer() {
        var context = CreateContext("/sse", "POST", new StubEntraAuthenticator(succeed: false));
        context.Request.Headers.Authorization = "Bearer secret";
        var nextCalled = false;
        var middleware = CreateMiddleware(
            Enabled: true,
            apiKey: "secret",
            next: _ => {
                nextCalled = true;
                return Task.CompletedTask;
            },
            entraEnabled: true);

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled);
        Assert.NotEqual(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    [Fact]
    public void EntraObjectIdGate_EmptyAllowList_AcceptsAnyPrincipal() {
        var principal = new System.Security.Claims.ClaimsPrincipal(
            new System.Security.Claims.ClaimsIdentity(
                [new System.Security.Claims.Claim("oid", "any-oid")],
                authenticationType: "test"));
        Assert.True(EntraBearerAuthenticator.IsObjectIdAllowed(principal, []));
    }

    [Fact]
    public void EntraObjectIdGate_NonEmptyAllowList_RequiresMatch() {
        var principal = new System.Security.Claims.ClaimsPrincipal(
            new System.Security.Claims.ClaimsIdentity(
                [new System.Security.Claims.Claim("oid", "allowed-oid")],
                authenticationType: "test"));
        Assert.True(EntraBearerAuthenticator.IsObjectIdAllowed(principal, ["allowed-oid"]));
        Assert.False(EntraBearerAuthenticator.IsObjectIdAllowed(principal, ["other-oid"]));
    }

    private static DefaultHttpContext CreateContext(
        string path,
        string method,
        IEntraBearerAuthenticator? entra = null) {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Request.Method = method;
        context.Response.Body = new MemoryStream();
        if (entra is not null) {
            var services = new ServiceCollection();
            services.AddSingleton(entra);
            context.RequestServices = services.BuildServiceProvider();
        }

        return context;
    }

    private static McpHttpAuthMiddleware CreateMiddleware(
        bool Enabled,
        string apiKey,
        RequestDelegate next,
        string? previousApiKey = null,
        bool entraEnabled = false) =>
        new(next, Options.Create(new McpServerOptions {
            HttpAuth = new HttpAuthOptions {
                Enabled = Enabled,
                ApiKey = apiKey,
                PreviousApiKey = previousApiKey ?? string.Empty,
                Entra = new EntraAuthOptions { Enabled = entraEnabled }
            }
        }));

    private sealed class StubEntraAuthenticator(bool succeed) : IEntraBearerAuthenticator {
        public Task<bool> TryAuthenticateAsync(
            HttpContext context,
            EntraAuthOptions options,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(succeed);
    }
}
