using System.Collections.Concurrent;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using UiPath.Engineering.Mcp.Core.Configuration;

namespace UiPath.Engineering.Mcp.Server.Tests;

/// <summary>
/// Pins the progress-notification contract over the real HTTP host. Progress is the one
/// server-to-client message the stateless HTTP transport allows, because it is scoped to the
/// in-flight request's progress token and travels back on that request's own stream. The tool
/// parameter that receives it is bound by the SDK and must stay out of the JSON schema.
/// </summary>
/// <remarks>
/// The host is configured with a nonexistent CLI executable so the test never launches the real
/// Studio CLI: the tools report their opening progress notification before the CLI is even
/// resolved, which is what this test needs to observe. That also keeps the test hermetic and fast.
/// </remarks>
public class ProgressNotificationTests {
    private const string ApiKey = "progress-test-api-key";

    [Fact]
    public async Task ProgressParameter_IsSdkBound_ExcludedFromSchema_AndReceivesNotifications() {
        var tempRoot = Path.Combine(Path.GetTempPath(), "mcp-progress-" + Guid.NewGuid().ToString("N"));
        var projectPath = Path.Combine(tempRoot, "ProgressProject");
        Directory.CreateDirectory(projectPath);
        await File.WriteAllTextAsync(
            Path.Combine(projectPath, "project.json"),
            """{"name":"ProgressProject","main":"Main.xaml"}""");
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
            await using var factory = new ProgressHostFactory(tempRoot);
            using var authorized = factory.CreateClient();
            authorized.DefaultRequestHeaders.Add("X-Api-Key", ApiKey);
            authorized.Timeout = TimeSpan.FromSeconds(60);

            await using var transport = new HttpClientTransport(
                new HttpClientTransportOptions {
                    Endpoint = new Uri(authorized.BaseAddress!, "sse"),
                    TransportMode = HttpTransportMode.StreamableHttp
                },
                authorized,
                ownsHttpClient: false);
            await using var client = await McpClient.CreateAsync(transport);

            var tools = (await client.ListToolsAsync()).ToDictionary(t => t.Name, StringComparer.Ordinal);

            // The progress sink is an SDK-bound parameter: it must NOT appear as a callable input
            // on any tool, because a caller must never be able to pass it as an ordinary argument.
            foreach (var tool in tools.Values) {
                Assert.False(
                    tool.JsonSchema.GetProperty("properties").TryGetProperty("progress", out _),
                    $"'{tool.Name}' must not advertise the SDK-bound progress parameter");
            }

            // A caller that supplies a progress token receives notifications for this call.
            var progress = new RecordingProgress();
            var call = await tools["validate_project"].CallAsync(
                new Dictionary<string, object?> { ["projectPath"] = projectPath },
                progress);

            Assert.NotNull(call);
            Assert.NotEmpty(progress.Values);

            // The opening notification is reported before the CLI is resolved, so it is always
            // observed; the SDK may dispatch the later ones out of order over the network.
            Assert.Contains(progress.Values, v => v.Progress == 0);
            Assert.All(progress.Values, v => Assert.NotNull(v.Message));
            Assert.All(progress.Values, v => Assert.True(v.Total is > 0));
            Assert.All(progress.Values, v => Assert.InRange(v.Progress, 0, v.Total!.Value));
            Assert.Contains(progress.Values, v => v.Message!.Contains("validate", StringComparison.OrdinalIgnoreCase));
        } finally {
            try { Directory.Delete(tempRoot, recursive: true); } catch (IOException) { }
        }
    }

    private sealed class RecordingProgress : IProgress<ProgressNotificationValue> {
        public ConcurrentQueue<ProgressNotificationValue> Recorded { get; } = new();
        public IReadOnlyList<ProgressNotificationValue> Values => Recorded.ToList();
        public void Report(ProgressNotificationValue value) => Recorded.Enqueue(value);
    }

    private sealed class ProgressHostFactory : WebApplicationFactory<Program> {
        private readonly string _allowedRoot;
        public ProgressHostFactory(string allowedRoot) => _allowedRoot = allowedRoot;

        protected override void ConfigureWebHost(IWebHostBuilder builder) {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, config) => {
                config.AddInMemoryCollection(new Dictionary<string, string?> {
                    ["Urls"] = "http://127.0.0.1:0",
                    ["McpServer:HttpAuth:Enabled"] = "true",
                    ["McpServer:HttpAuth:ApiKey"] = ApiKey,
                    ["McpServer:ToolSurface"] = CopilotConnectorTools.SurfaceAll,
                    ["Projects:AllowedRoots:0"] = _allowedRoot,
                    // Point the provider at a path that cannot exist so no real Studio CLI runs.
                    ["UiPathCli:ExecutablePath"] = Path.Combine(_allowedRoot, "no-such-uip")
                });
            });
        }
    }
}