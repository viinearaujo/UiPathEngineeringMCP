using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using UiPath.Engineering.Mcp.Core.Configuration;
using UiPath.Engineering.Mcp.Providers.UiPathCli;

namespace UiPath.Engineering.Mcp.Server;

internal sealed class McpReadinessHealthCheck : IHealthCheck {
    private readonly ProjectRootOptions _roots;
    private readonly HttpAuthOptions _auth;
    private readonly UiPathCliOptions _cli;
    private readonly IHostEnvironment _environment;

    public McpReadinessHealthCheck(
        IOptions<ProjectRootOptions> roots,
        IOptions<McpServerOptions> server,
        IOptions<UiPathCliOptions> cli,
        IHostEnvironment environment) {
        _roots = roots.Value;
        _auth = server.Value.HttpAuth;
        _cli = cli.Value;
        _environment = environment;
    }

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default) {
        var cliPresent = CliAvailability.IsPresent(_cli.ExecutablePath);
        return Task.FromResult(Evaluate(_roots.AllowedRoots, _auth, _environment.EnvironmentName, cliPresent));
    }

    internal static HealthCheckResult Evaluate(
        IReadOnlyList<string> allowedRoots,
        HttpAuthOptions auth,
        string? environmentName,
        bool cliPresent) {
        var hasRoots = allowedRoots.Any(r => !string.IsNullOrWhiteSpace(r));
        var authError = HttpAuthEvaluator.ValidateHttpStartup(auth, environmentName);
        var data = new Dictionary<string, object> {
            ["allowedRoots"] = hasRoots,
            ["authConfigured"] = authError is null,
            ["cliPresent"] = cliPresent
        };

        if (!hasRoots) {
            return HealthCheckResult.Unhealthy(
                "HTTP readiness requires a non-empty Projects:AllowedRoots.",
                data: data);
        }

        if (authError is not null) {
            return HealthCheckResult.Unhealthy(authError, data: data);
        }

        if (!cliPresent) {
            return HealthCheckResult.Degraded(
                "UiPath CLI (uip) is not on PATH. CLI tools will fail until it is installed or UiPathCli:ExecutablePath is set.",
                data: data);
        }

        return HealthCheckResult.Healthy("Ready.", data: data);
    }
}
