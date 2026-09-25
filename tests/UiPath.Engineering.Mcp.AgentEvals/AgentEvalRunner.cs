using System.Text.Json.Nodes;

namespace UiPath.Engineering.Mcp.AgentEvals;

internal sealed class AgentEvalRunner {
    private readonly string _repoRoot;
    private readonly OpenAiCompatibleClient _llm;

    public AgentEvalRunner(string repoRoot, OpenAiCompatibleClient llm) {
        _repoRoot = repoRoot;
        _llm = llm;
    }

    public async Task<IReadOnlyList<AgentTaskResult>> RunAllAsync(
        IReadOnlyList<AgentTaskDefinition> tasks,
        CancellationToken cancellationToken = default) {
        var workspaceRoot = Path.Combine(Path.GetTempPath(), "mcp-agent-eval-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspaceRoot);

        var serverProject = Path.Combine(
            _repoRoot, "src", "UiPath.Engineering.Mcp.Server", "UiPath.Engineering.Mcp.Server.csproj");
        if (!File.Exists(serverProject)) {
            throw new FileNotFoundException("MCP server project not found.", serverProject);
        }

        McpStdioSession? mcp = null;
        var results = new List<AgentTaskResult>(tasks.Count);
        try {
            mcp = await McpStdioSession.StartAsync(serverProject, workspaceRoot, cancellationToken);
            var tools = await mcp.ListToolsAsync(cancellationToken);
            if (tools.Count == 0) {
                throw new InvalidOperationException("MCP tools/list returned zero tools.");
            }

            foreach (var task in tasks) {
                cancellationToken.ThrowIfCancellationRequested();
                results.Add(await RunOneAsync(mcp, tools, workspaceRoot, task, cancellationToken));
            }

            return results;
        } finally {
            if (mcp is not null) {
                await mcp.DisposeAsync();
            }

            try {
                Directory.Delete(workspaceRoot, recursive: true);
            } catch (IOException) {
                // best-effort
            } catch (UnauthorizedAccessException) {
                // best-effort
            }
        }
    }

    private async Task<AgentTaskResult> RunOneAsync(
        McpStdioSession mcp,
        IReadOnlyList<McpToolInfo> tools,
        string workspaceRoot,
        AgentTaskDefinition task,
        CancellationToken cancellationToken) {
        var projectPath = Path.Combine(workspaceRoot, task.Id);
        AgentTaskLoader.MaterializeFixture(_repoRoot, task.Fixture, projectPath);

        var prompt = task.Prompt.Replace("{{projectPath}}", projectPath, StringComparison.Ordinal);
        var toolNames = tools.Select(t => t.Name).ToArray();
        var messages = new List<JsonObject> {
            new() {
                ["role"] = "system",
                ["content"] = AgentSystemPrompt.Build(projectPath, toolNames)
            },
            new() {
                ["role"] = "user",
                ["content"] = prompt
            }
        };

        var rounds = 0;
        string? loopError = null;
        try {
            for (var i = 0; i < AgentEvalConfig.MaxToolRounds; i++) {
                var completion = await _llm.ChatAsync(
                    AgentEvalConfig.Model, messages, tools, cancellationToken);
                messages.Add(completion.AssistantMessage);

                if (completion.ToolCalls.Count == 0) {
                    break;
                }

                rounds++;
                foreach (var call in completion.ToolCalls) {
                    string toolResult;
                    try {
                        toolResult = await mcp.CallToolAsync(call.Name, call.Arguments, cancellationToken);
                    } catch (Exception ex) {
                        toolResult = $"{{\"error\":\"{ex.Message.Replace("\"", "'", StringComparison.Ordinal)}\"}}";
                    }

                    messages.Add(new JsonObject {
                        ["role"] = "tool",
                        ["tool_call_id"] = call.Id,
                        ["content"] = toolResult
                    });
                }
            }
        } catch (Exception ex) {
            loopError = ex.Message;
        }

        var (passed, detail) = AssertionChecker.Evaluate(projectPath, task.Assertions);
        if (loopError is not null) {
            passed = false;
            detail = $"loop error: {loopError}; {detail}";
        }

        return new AgentTaskResult {
            Id = task.Id,
            Passed = passed,
            Detail = detail,
            ToolRounds = rounds
        };
    }
}
