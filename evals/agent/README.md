# Agent evals (LLM-in-the-loop)

Scripted tasks live in `evals/agent/tasks/`. Tiny fixtures live in `evals/agent/fixtures/`.

## Run

```powershell
$env:AGENT_EVAL_API_KEY = "<key>"
$env:AGENT_EVAL_MODEL = "gpt-4.1-mini"
# optional:
# $env:AGENT_EVAL_BASE_URL = "https://api.openai.com/v1"

dotnet test tests/UiPath.Engineering.Mcp.AgentEvals --filter Category=AgentEval
```

The harness starts the MCP server with `--stdio`, discovers tools via `tools/list`, and drives an OpenAI-compatible chat loop (max 12 tool rounds per task). Results overwrite `evals/agent/scorecard.md`.

When the API key or model env var is missing, the test skips (does not fail).
