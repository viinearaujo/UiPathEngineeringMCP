using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using ModelContextProtocol.Client;
using UiPath.Engineering.Mcp.Core.Configuration;

namespace UiPath.Engineering.Mcp.Server.Tests;

[Collection(McpHostCollection.Name)]
public class McpHostTests {
    private const string ApiKey = "host-test-api-key";

    [Fact]
    public async Task HttpHost_HealthOk_SseUnauthorizedWithoutKey_ListsCopilotTools_AndAnalyzesTempProject() {
        var tempRoot = Path.Combine(Path.GetTempPath(), "mcp-host-" + Guid.NewGuid().ToString("N"));
        var projectPath = Path.Combine(tempRoot, "HostTestProject");
        Directory.CreateDirectory(projectPath);
        await File.WriteAllTextAsync(
            Path.Combine(projectPath, "project.json"),
            """{"name":"HostTestProject","description":"Host fixture","main":"Main.xaml"}""");
        await File.WriteAllTextAsync(
            Path.Combine(projectPath, "Main.xaml"),
            """
            <Activity x:Class="Main"
              xmlns="http://schemas.microsoft.com/netfx/2009/xaml/activities"
              xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
              <Sequence DisplayName="Main" />
            </Activity>
            """);

        try {
            await using var factory = new McpHostFactory(tempRoot);
            using var anonymous = factory.CreateClient();

            var health = await anonymous.GetAsync("/health");
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);

            var sse = await anonymous.GetAsync("/sse");
            Assert.Equal(HttpStatusCode.Unauthorized, sse.StatusCode);

            using var authorized = factory.CreateClient();
            authorized.DefaultRequestHeaders.Add("X-Api-Key", ApiKey);
            authorized.Timeout = TimeSpan.FromSeconds(30);

            await using var transport = new HttpClientTransport(
                new HttpClientTransportOptions {
                    Endpoint = new Uri(authorized.BaseAddress!, "sse"),
                    TransportMode = HttpTransportMode.StreamableHttp
                },
                authorized,
                ownsHttpClient: false);
            await using var client = await McpClient.CreateAsync(transport);

            var listed = await client.ListToolsAsync();
            var names = listed.Select(t => t.Name).ToArray();
            Assert.Equal(
                CopilotConnectorTools.DefaultNames.OrderBy(n => n, StringComparer.Ordinal),
                names.OrderBy(n => n, StringComparer.Ordinal));
            Assert.True(names.Length <= CopilotConnectorTools.MaxDefaultCount);
            foreach (var leaveOff in CopilotConnectorTools.LeaveOffNames) {
                Assert.DoesNotContain(leaveOff, names);
            }

            var call = await client.CallToolAsync(
                "analyze_project",
                new Dictionary<string, object?> { ["projectPath"] = projectPath });
            Assert.True(call.IsError is not true, call.Content.FirstOrDefault()?.ToString());

            using var structured = JsonDocument.Parse(call.StructuredContent?.GetRawText() ?? "{}");
            var root = structured.RootElement;
            Assert.Equal("success", root.GetProperty("status").GetString());
            Assert.Equal(
                "HostTestProject",
                root.GetProperty("data").GetProperty("summary").GetProperty("projectName").GetString());
        } finally {
            try {
                Directory.Delete(tempRoot, recursive: true);
            } catch (IOException) {
                // Best-effort cleanup of the temp project.
            }
        }
    }

    [Fact]
    public async Task HttpHost_ServerInfo_ComesFromMcpServerConfig() {
        var tempRoot = Path.Combine(Path.GetTempPath(), "mcp-host-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try {
            await using var factory = new McpHostFactory(tempRoot, new Dictionary<string, string?> {
                ["McpServer:Name"] = "UiPath Engineering MCP",
                ["McpServer:Version"] = "9.9.9",
                ["McpServer:Description"] = "Config-bound server identity."
            });
            using var authorized = factory.CreateClient();
            authorized.DefaultRequestHeaders.Add("X-Api-Key", ApiKey);
            authorized.Timeout = TimeSpan.FromSeconds(30);

            await using var transport = new HttpClientTransport(
                new HttpClientTransportOptions {
                    Endpoint = new Uri(authorized.BaseAddress!, "sse"),
                    TransportMode = HttpTransportMode.StreamableHttp
                },
                authorized,
                ownsHttpClient: false);
            await using var client = await McpClient.CreateAsync(transport);

            Assert.Equal("UiPath Engineering MCP", client.ServerInfo.Name);
            Assert.Equal("9.9.9", client.ServerInfo.Version);
            Assert.Equal("Config-bound server identity.", client.ServerInfo.Description);
        } finally {
            try {
                Directory.Delete(tempRoot, recursive: true);
            } catch (IOException) {
                // Best-effort cleanup.
            }
        }
    }

    [Fact]
    public async Task HttpHost_AnalyzeProject_InvalidProjectJson_MapsExceptionWithoutLeakingParserText() {
        var tempRoot = Path.Combine(Path.GetTempPath(), "mcp-host-" + Guid.NewGuid().ToString("N"));
        var projectPath = Path.Combine(tempRoot, "HostTestProject");
        Directory.CreateDirectory(projectPath);
        await File.WriteAllTextAsync(Path.Combine(projectPath, "project.json"), "{not-json");

        try {
            await using var factory = new McpHostFactory(tempRoot);
            using var authorized = factory.CreateClient();
            authorized.DefaultRequestHeaders.Add("X-Api-Key", ApiKey);
            authorized.Timeout = TimeSpan.FromSeconds(30);

            await using var transport = new HttpClientTransport(
                new HttpClientTransportOptions {
                    Endpoint = new Uri(authorized.BaseAddress!, "sse"),
                    TransportMode = HttpTransportMode.StreamableHttp
                },
                authorized,
                ownsHttpClient: false);
            await using var client = await McpClient.CreateAsync(transport);

            var call = await client.CallToolAsync(
                "analyze_project",
                new Dictionary<string, object?> { ["projectPath"] = projectPath });

            Assert.True(call.IsError is true);
            using var structured = JsonDocument.Parse(call.StructuredContent?.GetRawText() ?? "{}");
            var raw = structured.RootElement.GetRawText();
            Assert.Contains("PROJECT_JSON_INVALID", raw);
            Assert.DoesNotContain("not-json", raw, StringComparison.OrdinalIgnoreCase);
            var status = structured.RootElement.TryGetProperty("status", out var camel)
                ? camel.GetString()
                : structured.RootElement.GetProperty("Status").GetString();
            Assert.Equal("error", status);
        } finally {
            try {
                Directory.Delete(tempRoot, recursive: true);
            } catch (IOException) {
                // Best-effort cleanup of the temp project.
            }
        }
    }

    private sealed class McpHostFactory : WebApplicationFactory<Program> {
        private readonly string _allowedRoot;
        private readonly Dictionary<string, string?> _overrides;

        public McpHostFactory(string allowedRoot, Dictionary<string, string?>? overrides = null) {
            _allowedRoot = allowedRoot;
            _overrides = overrides ?? [];
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder) {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, config) => {
                var settings = new Dictionary<string, string?> {
                    ["Urls"] = "http://127.0.0.1:0",
                    ["McpServer:HttpAuth:Enabled"] = "true",
                    ["McpServer:HttpAuth:ApiKey"] = ApiKey,
                    ["McpServer:ToolSurface"] = CopilotConnectorTools.SurfaceCopilotDefault,
                    ["Projects:AllowedRoots:0"] = _allowedRoot
                };
                foreach (var pair in _overrides) {
                    settings[pair.Key] = pair.Value;
                }

                config.AddInMemoryCollection(settings);
            });
        }
    }
}
