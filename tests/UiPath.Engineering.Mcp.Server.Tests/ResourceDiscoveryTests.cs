using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using ModelContextProtocol.Client;
using UiPath.Engineering.Mcp.Core.Configuration;

namespace UiPath.Engineering.Mcp.Server.Tests;

/// <summary>
/// Pins the resource discovery contract over the real HTTP host. Every resource this server
/// registers is a URI template, so <c>resources/list</c> returns an empty array and the
/// templates reach the client through <c>resources/listResourceTemplates</c> instead — the two
/// facts the Copilot Studio registration depends on (documented in
/// <c>docs/agent-connection.md</c> § Resources).
/// </summary>
public class ResourceDiscoveryTests {
    private const string ApiKey = "resource-discovery-api-key";

    [Fact]
    public async Task ResourcesList_IsEmpty_TemplatesCarryEveryResource_AndActivityResourceReads() {
        var tempRoot = Path.Combine(Path.GetTempPath(), "mcp-resources-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try {
            await using var factory = new ResourceHostFactory(tempRoot);
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

            // No static resource exists: a client that reads only resources/list sees none.
            var listed = await client.ListResourcesAsync();
            Assert.Empty(listed);

            // The templates are where every resource lives. Count is not pinned (the set grows
            // with the catalog); the templates the knowledge surface added must be present.
            var templates = await client.ListResourceTemplatesAsync();
            var uris = templates.Select(t => t.UriTemplate).ToArray();
            Assert.Contains("uipath://skills/{name}", uris);
            Assert.Contains("uipath://activity/{projectPath}/{name}", uris);
            Assert.Contains("uipath://idioms/{name}", uris);

            // A concrete URI built from the activity template reads back a camelCase surface
            // from the built-in fallback catalog, which needs no project.
            var read = await client.ReadResourceAsync("uipath://activity/global/LogMessage");
            var text = read.Contents.OfType<ModelContextProtocol.Protocol.TextResourceContents>().Single().Text;
            using var payload = JsonDocument.Parse(text);
            Assert.Equal("LogMessage", payload.RootElement.GetProperty("name").GetString());
            Assert.Equal("ui", payload.RootElement.GetProperty("prefix").GetString());
            Assert.True(payload.RootElement.GetProperty("properties").GetArrayLength() > 0);

            // An unknown activity is a structured error, not a thrown exception or an empty body.
            var missing = await client.ReadResourceAsync("uipath://activity/global/NoSuchActivity");
            var missingText = missing.Contents.OfType<ModelContextProtocol.Protocol.TextResourceContents>().Single().Text;
            Assert.Contains("ACTIVITY_NOT_FOUND", missingText);
        } finally {
            try { Directory.Delete(tempRoot, recursive: true); } catch (IOException) { }
        }
    }

    private sealed class ResourceHostFactory : WebApplicationFactory<Program> {
        private readonly string _allowedRoot;
        public ResourceHostFactory(string allowedRoot) => _allowedRoot = allowedRoot;

        protected override void ConfigureWebHost(IWebHostBuilder builder) {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, config) => {
                config.AddInMemoryCollection(new Dictionary<string, string?> {
                    ["Urls"] = "http://127.0.0.1:0",
                    ["McpServer:HttpAuth:Enabled"] = "true",
                    ["McpServer:HttpAuth:ApiKey"] = ApiKey,
                    ["McpServer:ToolSurface"] = CopilotConnectorTools.SurfaceAll,
                    ["Projects:AllowedRoots:0"] = _allowedRoot
                });
            });
        }
    }
}