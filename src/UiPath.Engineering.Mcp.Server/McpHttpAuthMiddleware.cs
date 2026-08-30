using Microsoft.Extensions.Options;
using UiPath.Engineering.Mcp.Core.Configuration;

namespace UiPath.Engineering.Mcp.Server;

public sealed class McpHttpAuthMiddleware {
    private readonly RequestDelegate _next;
    private readonly HttpAuthOptions _options;

    public McpHttpAuthMiddleware(RequestDelegate next, IOptions<McpServerOptions> serverOptions) {
        _next = next;
        _options = serverOptions.Value.HttpAuth;
    }

    public async Task InvokeAsync(HttpContext context) {
        var path = context.Request.Path.Value ?? string.Empty;
        if (HttpAuthEvaluator.IsAnonymousPath(path) || !HttpAuthEvaluator.IsProtectedPath(path)) {
            await _next(context);
            return;
        }

        if (HttpMethods.IsOptions(context.Request.Method)) {
            await _next(context);
            return;
        }

        var headerName = HttpAuthEvaluator.ResolveHeaderName(_options);
        context.Request.Headers.TryGetValue(headerName, out var apiKey);
        var authorization = context.Request.Headers.Authorization.ToString();
        if (HttpAuthEvaluator.IsAuthorized(_options, apiKey.ToString(), string.IsNullOrEmpty(authorization) ? null : authorization)) {
            await _next(context);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.Headers.WWWAuthenticate = $"ApiKey realm=\"mcp\", header=\"{headerName}\"";
    }
}
