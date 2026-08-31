using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using UiPath.Engineering.Mcp.Core.Configuration;

namespace UiPath.Engineering.Mcp.Server;

public sealed class McpHttpAuthMiddleware {
    private readonly RequestDelegate _next;
    private readonly HttpAuthOptions _options;
    private readonly ILogger<McpHttpAuthMiddleware> _logger;

    public McpHttpAuthMiddleware(
        RequestDelegate next,
        IOptions<McpServerOptions> serverOptions,
        ILogger<McpHttpAuthMiddleware>? logger = null) {
        _next = next;
        _options = serverOptions.Value.HttpAuth;
        _logger = logger ?? NullLogger<McpHttpAuthMiddleware>.Instance;
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
            var sw = Stopwatch.StartNew();
            await _next(context);
            sw.Stop();
            _logger.LogDebug(
                "MCP HTTP {Method} {Path} duration {DurationMs}ms status {Status}",
                context.Request.Method,
                path,
                sw.ElapsedMilliseconds,
                context.Response.StatusCode);
            return;
        }

        // Never log header values, Authorization, or API keys — only that the gate rejected.
        _logger.LogWarning(
            "MCP HTTP {Method} {Path} duration {DurationMs}ms status {Status} errorCode {ErrorCode}",
            context.Request.Method,
            path,
            0,
            StatusCodes.Status401Unauthorized,
            "unauthorized");
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.Headers.WWWAuthenticate = $"ApiKey realm=\"mcp\", header=\"{headerName}\"";
    }
}
