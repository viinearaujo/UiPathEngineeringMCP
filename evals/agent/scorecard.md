# Agent eval scorecard

No LLM run recorded yet.

Set `AGENT_EVAL_API_KEY` and `AGENT_EVAL_MODEL` (optional `AGENT_EVAL_BASE_URL`, default `https://api.openai.com/v1`), then:

```
dotnet test tests/UiPath.Engineering.Mcp.AgentEvals --filter Category=AgentEval
```

Without those env vars the AgentEval test calls `Assert.Skip` and does not fail CI. Pull-request CI does not require a live model.
