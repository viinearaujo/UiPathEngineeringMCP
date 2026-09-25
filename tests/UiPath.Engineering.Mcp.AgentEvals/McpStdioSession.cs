using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Client;

namespace UiPath.Engineering.Mcp.AgentEvals;

/// <summary>
/// MCP stdio session via the official SDK client. Discovers tools with tools/list at runtime.
/// </summary>
internal sealed class McpStdioSession : IAsyncDisposable {
    private readonly McpClient _client;

    private McpStdioSession(McpClient client) {
        _client = client;
    }

    public static async Task<McpStdioSession> StartAsync(
        string serverProjectPath,
        string allowedRoot,
        CancellationToken cancellationToken = default) {
        var transport = new StdioClientTransport(new StdioClientTransportOptions {
            Name = "uipath-agent-evals",
            Command = "dotnet",
            Arguments = [
                "run",
                "--project",
                serverProjectPath,
                "--no-launch-profile",
                "--",
                "--stdio"
            ],
            WorkingDirectory = Path.GetDirectoryName(serverProjectPath)!,
            InheritEnvironmentVariables = true,
            EnvironmentVariables = new Dictionary<string, string?> {
                ["Projects__AllowedRoots__0"] = allowedRoot,
                ["McpServer__ToolSurface"] = "All"
            }
        });

        var client = await McpClient.CreateAsync(transport, cancellationToken: cancellationToken);
        return new McpStdioSession(client);
    }

    public async Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(CancellationToken cancellationToken = default) {
        var listed = await _client.ListToolsAsync(cancellationToken: cancellationToken);
        var tools = new List<McpToolInfo>(listed.Count);
        foreach (var tool in listed) {
            JsonNode schema;
            try {
                schema = JsonNode.Parse(tool.JsonSchema.GetRawText())
                    ?? new JsonObject { ["type"] = "object" };
            } catch (JsonException) {
                schema = new JsonObject { ["type"] = "object" };
            }

            tools.Add(new McpToolInfo {
                Name = tool.Name,
                Description = tool.Description ?? "",
                InputSchema = schema
            });
        }

        return tools;
    }

    public async Task<string> CallToolAsync(
        string name,
        JsonNode? arguments,
        CancellationToken cancellationToken = default) {
        var args = ToArgumentDictionary(arguments);
        var result = await _client.CallToolAsync(name, args, cancellationToken: cancellationToken);
        if (result.StructuredContent is { } structured) {
            return structured.GetRawText();
        }

        if (result.Content is { Count: > 0 }) {
            return JsonSerializer.Serialize(result.Content);
        }

        return "{}";
    }

    private static IReadOnlyDictionary<string, object?> ToArgumentDictionary(JsonNode? arguments) {
        if (arguments is not JsonObject obj) {
            return new Dictionary<string, object?>();
        }

        var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in obj) {
            dict[key] = value is null ? null : JsonSerializer.Deserialize<object>(value.ToJsonString());
        }

        return dict;
    }

    public ValueTask DisposeAsync() => _client.DisposeAsync();
}

internal sealed class McpToolInfo {
    public required string Name { get; init; }
    public string Description { get; init; } = "";
    public required JsonNode InputSchema { get; init; }
}
