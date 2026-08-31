using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using UiPath.Engineering.Mcp.Core.Configuration;
using UiPath.Engineering.Mcp.Server;

namespace UiPath.Engineering.Mcp.Server.Tests;

public class McpReadinessHealthCheckTests {
    [Fact]
    public void Evaluate_EmptyAllowedRoots_IsUnhealthy() {
        var result = McpReadinessHealthCheck.Evaluate(
            [],
            new HttpAuthOptions { Enabled = true, ApiKey = "secret" },
            "Production",
            cliPresent: true);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("AllowedRoots", result.Description);
    }

    [Fact]
    public void Evaluate_ProductionWithoutAuth_IsUnhealthy() {
        var result = McpReadinessHealthCheck.Evaluate(
            [@"C:\projects"],
            new HttpAuthOptions { Enabled = false, ApiKey = "" },
            "Production",
            cliPresent: true);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("HttpAuth", result.Description);
    }

    [Fact]
    public void Evaluate_DevelopmentWithoutAuth_WithRootsAndCli_IsHealthy() {
        var result = McpReadinessHealthCheck.Evaluate(
            [@"C:\projects"],
            new HttpAuthOptions { Enabled = false, ApiKey = "" },
            "Development",
            cliPresent: true);

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public void Evaluate_MissingCli_IsDegraded() {
        var result = McpReadinessHealthCheck.Evaluate(
            [@"C:\projects"],
            new HttpAuthOptions { Enabled = true, ApiKey = "secret" },
            "Production",
            cliPresent: false);

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Contains("CLI", result.Description);
        Assert.Equal(false, result.Data["cliPresent"]);
        Assert.Equal(true, result.Data["allowedRoots"]);
        Assert.Equal(true, result.Data["authConfigured"]);
    }

    [Fact]
    public async Task CheckHealthAsync_EmptyRoots_IsUnhealthy() {
        var sut = new McpReadinessHealthCheck(
            Options.Create(new ProjectRootOptions { AllowedRoots = [] }),
            Options.Create(new McpServerOptions {
                HttpAuth = new HttpAuthOptions { Enabled = true, ApiKey = "secret" }
            }),
            Options.Create(new UiPathCliOptions { ExecutablePath = "definitely-not-a-real-uip-xyz" }),
            new FakeHostEnvironment { EnvironmentName = "Development" });

        var result = await sut.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Equal(false, result.Data["cliPresent"]);
    }

    [Fact]
    public void HealthEndpoints_UseDistinctLivenessAndReadinessPaths() {
        Assert.Equal("/health", McpHealthEndpoints.LivenessPath);
        Assert.Equal("/health/ready", McpHealthEndpoints.ReadinessPath);
        Assert.Equal("ready", McpHealthEndpoints.ReadyTag);
    }

    private sealed class FakeHostEnvironment : IHostEnvironment {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "test";
        public string ContentRootPath { get; set; } = "/";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
