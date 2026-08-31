using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using UiPath.Engineering.Mcp.Core.Configuration;
using UiPath.Engineering.Mcp.Server;

namespace UiPath.Engineering.Mcp.Server.Tests;

public class McpHttpAuthOptionsValidatorTests {
    [Fact]
    public void Validate_Development_AllowsDisabledAuth() {
        var result = new McpHttpAuthOptionsValidator(new StubHostEnvironment("Development"))
            .Validate(Options.DefaultName, new McpServerOptions {
                HttpAuth = new HttpAuthOptions { Enabled = false, ApiKey = "" }
            });

        Assert.False(result.Failed);
    }

    [Fact]
    public void Validate_Production_FailsWhenAuthDisabled() {
        var result = new McpHttpAuthOptionsValidator(new StubHostEnvironment("Production"))
            .Validate(Options.DefaultName, new McpServerOptions {
                HttpAuth = new HttpAuthOptions { Enabled = false, ApiKey = "secret" }
            });

        Assert.True(result.Failed);
        Assert.Contains("HttpAuth:Enabled", result.FailureMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_Production_FailsWhenKeyEmpty() {
        var result = new McpHttpAuthOptionsValidator(new StubHostEnvironment("Production"))
            .Validate(Options.DefaultName, new McpServerOptions {
                HttpAuth = new HttpAuthOptions { Enabled = true, ApiKey = "" }
            });

        Assert.True(result.Failed);
        Assert.Contains("HttpAuth:ApiKey", result.FailureMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_Production_SucceedsWhenEnabledWithKey() {
        var result = new McpHttpAuthOptionsValidator(new StubHostEnvironment("Production"))
            .Validate(Options.DefaultName, new McpServerOptions {
                HttpAuth = new HttpAuthOptions { Enabled = true, ApiKey = "secret" }
            });

        Assert.False(result.Failed);
    }

    [Fact]
    public void OptionsValue_ProductionWithoutAuth_Throws() {
        var services = new ServiceCollection();
        services.AddSingleton<IHostEnvironment>(new StubHostEnvironment("Production"));
        services.Configure<McpServerOptions>(options => {
            options.HttpAuth = new HttpAuthOptions { Enabled = false, ApiKey = "" };
        });
        services.AddSingleton<IValidateOptions<McpServerOptions>, McpHttpAuthOptionsValidator>();
        services.AddOptions<McpServerOptions>().ValidateOnStart();

        using var provider = services.BuildServiceProvider();
        var ex = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<McpServerOptions>>().Value);
        Assert.Contains("HttpAuth:Enabled", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CommittedAppsettings_ShipsEmptyAllowedRoots() {
        using var doc = JsonDocument.Parse(ReadRepoFile(Path.Combine(
            "src", "UiPath.Engineering.Mcp.Server", "appsettings.json")));
        var roots = doc.RootElement.GetProperty("Projects").GetProperty("AllowedRoots");
        Assert.Equal(JsonValueKind.Array, roots.ValueKind);
        Assert.Equal(0, roots.GetArrayLength());
    }

    private static string ReadRepoFile(string relativePath) {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null) {
            var candidate = Path.Combine(dir.FullName, relativePath);
            if (File.Exists(candidate)) {
                return File.ReadAllText(candidate);
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException($"Could not locate '{relativePath}' from the test output directory.");
    }

    private sealed class StubHostEnvironment : IHostEnvironment {
        public StubHostEnvironment(string environmentName) => EnvironmentName = environmentName;
        public string EnvironmentName { get; set; }
        public string ApplicationName { get; set; } = "UiPath.Engineering.Mcp.Server.Tests";
        public string ContentRootPath { get; set; } = ".";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
}
