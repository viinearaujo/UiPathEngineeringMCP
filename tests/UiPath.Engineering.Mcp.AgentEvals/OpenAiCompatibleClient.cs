using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace UiPath.Engineering.Mcp.AgentEvals;

internal sealed class OpenAiCompatibleClient : IDisposable {
    private readonly HttpClient _http;

    public OpenAiCompatibleClient(string baseUrl, string apiKey) {
        _http = new HttpClient {
            BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromMinutes(4)
        };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
    }

    public async Task<ChatCompletionResult> ChatAsync(
        string model,
        IReadOnlyList<JsonObject> messages,
        IReadOnlyList<McpToolInfo> tools,
        CancellationToken cancellationToken = default) {
        var toolDefs = new JsonArray();
        foreach (var tool in tools) {
            toolDefs.Add(new JsonObject {
                ["type"] = "function",
                ["function"] = new JsonObject {
                    ["name"] = tool.Name,
                    ["description"] = Truncate(tool.Description, 400),
                    ["parameters"] = tool.InputSchema.DeepClone()
                }
            });
        }

        var body = new JsonObject {
            ["model"] = model,
            ["messages"] = new JsonArray(messages.Select(m => m.DeepClone()).ToArray()),
            ["tools"] = toolDefs,
            ["tool_choice"] = "auto"
        };

        using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync("chat/completions", content, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode) {
            throw new InvalidOperationException(
                $"Chat completions failed ({(int)response.StatusCode}): {Truncate(raw, 1000)}");
        }

        var doc = JsonNode.Parse(raw) as JsonObject
            ?? throw new InvalidOperationException("Chat completions returned non-object JSON.");
        var choice = doc["choices"]?[0] as JsonObject
            ?? throw new InvalidOperationException("Chat completions returned no choices.");
        var message = choice["message"] as JsonObject
            ?? throw new InvalidOperationException("Chat completions choice missing message.");

        var toolCalls = new List<ToolCall>();
        if (message["tool_calls"] is JsonArray calls) {
            foreach (var call in calls) {
                if (call is not JsonObject callObj) {
                    continue;
                }

                var id = callObj["id"]?.GetValue<string>() ?? Guid.NewGuid().ToString("N");
                var fn = callObj["function"] as JsonObject;
                var name = fn?["name"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(name)) {
                    continue;
                }

                var argsText = fn?["arguments"]?.GetValue<string>() ?? "{}";
                JsonNode? argsNode;
                try {
                    argsNode = JsonNode.Parse(argsText);
                } catch (JsonException) {
                    argsNode = new JsonObject();
                }

                toolCalls.Add(new ToolCall(id, name, argsNode));
            }
        }

        return new ChatCompletionResult(
            message.DeepClone().AsObject(),
            toolCalls,
            message["content"]?.GetValue<string>());
    }

    public void Dispose() => _http.Dispose();

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";
}

internal sealed record ToolCall(string Id, string Name, JsonNode? Arguments);

internal sealed record ChatCompletionResult(
    JsonObject AssistantMessage,
    IReadOnlyList<ToolCall> ToolCalls,
    string? Content);

internal static class AgentSystemPrompt {
    public static string Build(string projectPath, IReadOnlyList<string> toolNames) {
        var hasCheckWork = toolNames.Any(n => string.Equals(n, "check_work", StringComparison.Ordinal));
        var gate = hasCheckWork
            ? "Prefer check_work when available; otherwise use validate_project with build:false and pack:false."
            : "Use validate_project with build:false and pack:false as the green gate (check_work may be absent).";

        return
            """
            You are evaluating UiPath Engineering MCP tools. You may ONLY call tools — do not claim to edit files yourself.
            Prefer coded-first authoring: add_coded_workflow for new work, then edit_workflow_file for small edits.
            After mutations: validate_project (build false, pack false), then analyze_project_gaps.
            """
            + gate
            + $"""

            Absolute project path for this task: {projectPath}
            Available tool names: {string.Join(", ", toolNames)}
            Stop when the user request is satisfied or tools cannot progress further.
            """;
    }
}
