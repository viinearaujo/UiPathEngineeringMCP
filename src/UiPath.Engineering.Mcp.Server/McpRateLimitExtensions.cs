using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using UiPath.Engineering.Mcp.Core.Configuration;

namespace UiPath.Engineering.Mcp.Server;

public static class McpRateLimitExtensions {
    public const string SsePolicyName = "mcp-sse";

    public static IServiceCollection AddMcpSseRateLimiter(this IServiceCollection services, RateLimitOptions options) {
        ArgumentNullException.ThrowIfNull(options);

        var permitLimit = options.PermitLimit > 0 ? options.PermitLimit : 60;
        var windowSeconds = options.WindowSeconds > 0 ? options.WindowSeconds : 60;
        var window = TimeSpan.FromSeconds(windowSeconds);

        services.AddRateLimiter(limiter => {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiter.AddPolicy(SsePolicyName, httpContext => {
                var partitionKey = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                return RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey,
                    _ => new FixedWindowRateLimiterOptions {
                        PermitLimit = permitLimit,
                        Window = window,
                        QueueLimit = 0,
                        AutoReplenishment = true
                    });
            });
        });

        return services;
    }
}
