using Microsoft.AspNetCore.Diagnostics.HealthChecks;

namespace UiPath.Engineering.Mcp.Server;

internal static class McpHealthEndpoints {
    public const string LivenessPath = "/health";
    public const string ReadinessPath = "/health/ready";
    public const string ReadyTag = "ready";

    public static void Map(WebApplication app) {
        app.MapHealthChecks(LivenessPath, new HealthCheckOptions {
            Predicate = _ => false
        });
        app.MapHealthChecks(ReadinessPath, new HealthCheckOptions {
            Predicate = check => check.Tags.Contains(ReadyTag)
        });
    }
}
