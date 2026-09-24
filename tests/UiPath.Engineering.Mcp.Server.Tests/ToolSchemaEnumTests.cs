using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using ModelContextProtocol.Client;
using UiPath.Engineering.Mcp.Core.Configuration;

namespace UiPath.Engineering.Mcp.Server.Tests;

/// <summary>
/// End-to-end proof that the <c>[AllowedValues]</c> declarations reach the client's tool schema as
/// JSON-schema <c>enum</c> values. The tools themselves are unit-tested in
/// <c>AllowedValuesAttributeTests</c>; this pins the protocol surface, which is what an LLM or a
/// completion client actually reads.
/// </summary>
public class ToolSchemaEnumTests {
    private const string ApiKey = "schema-test-api-key";

    [Fact]
    public async Task ToolSchemas_ExposeAllowedValuesAsEnum() {
        var tempRoot = Path.Combine(Path.GetTempPath(), "mcp-schema-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try {
            await using var factory = new SchemaHostFactory(tempRoot);
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

            var tools = (await client.ListToolsAsync()).ToDictionary(t => t.Name, StringComparer.Ordinal);

            AssertEnum(tools["create_project"].JsonSchema, "expressionLanguage", "CSharp", "VisualBasic");
            AssertEnum(tools["create_project"].JsonSchema, "targetFramework", "Windows", "Portable");
            AssertEnum(tools["analyze_project"].JsonSchema, "detail", "summary", "full");
            AssertEnum(tools["add_coded_workflow"].JsonSchema, "kind", "workflow", "test", "source");
            AssertEnum(tools["search_codebase"].JsonSchema, "mode", "text", "symbol", "activity", "workflow");
            AssertEnum(tools["manage_workflow_data"].JsonSchema, "operation", "add", "remove", "rename");
            AssertEnum(tools["update_plan_task"].JsonSchema, "status", "pending", "in_progress", "done", "blocked");
            AssertEnum(tools["insert_activities"].JsonSchema, "position", "first", "last");
            AssertEnum(tools["manage_packages"].JsonSchema, "operation", "install", "versions", "inspect");
            AssertEnum(tools["get_object_repository"].JsonSchema, "source", "project", "library");
            AssertEnum(tools["get_analyzer_rules"].JsonSchema, "minSeverity", "error", "warning", "info");
        } finally {
            try { Directory.Delete(tempRoot, recursive: true); } catch (IOException) { }
        }
    }

    private static void AssertEnum(JsonElement schema, string parameter, params string[] expected) {
        var values = schema.GetProperty("properties").GetProperty(parameter).GetProperty("enum")
            .EnumerateArray().Select(v => v.GetString()).ToArray();
        Assert.Equal(expected, values);
    }

    private sealed class SchemaHostFactory : WebApplicationFactory<Program> {
        private readonly string _allowedRoot;
        public SchemaHostFactory(string allowedRoot) => _allowedRoot = allowedRoot;

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