# Connecting to UiPath Engineering MCP

The server is passive. The client drives the loop. UiPath facts come from tools; the harness owns retries and sequencing.

## HTTP (Copilot, Inspector, Dev Tunnel)

1. `dotnet run --project src/UiPath.Engineering.Mcp.Server` — listens on `http://localhost:5000`.
2. MCP Streamable HTTP endpoint: `http://localhost:5000/sse` (path name is historical; this is not legacy SSE).
3. Health: `GET http://localhost:5000/health`.
4. Copilot registration endpoint is the Dev Tunnel URL plus `/sse`. See README §4–§5.

## stdio (local agents)

```text
dotnet run --project src/UiPath.Engineering.Mcp.Server -- --stdio
```

Logs go to stderr. stdin/stdout are the MCP stream. Spawn this process from the agent host; do not also bind port 5000 in that process.

## Plan path (P0)

Tools resolve **only** `docs/implementation-plan.json` inside the target UiPath project (`ImplementationPlanStore`). Do not move it. `create_implementation_plan` overwrites that file when `overwrite: true` — on a mature plan, call `get_implementation_plan` / `update_plan_task` instead.

## Safe authoring loop

```text
analyze_project (detail=summary) → analyze_project_gaps (treat many hits as noise)
  → get_implementation_plan; create_implementation_plan only if none exists
  → author one task
  → search_codebase / read_workflow_file to confirm the write
  → validate_project(build:false, pack:false)
  → update_plan_task(done|blocked)
```

Close tasks with `validate_project(build:false, pack:false)` then `update_plan_task`. `verify_work` defaults `build: false` and does not auto-block on a build-only failure; it is still not the green gate.

File truth is `read_workflow_file` / `search_codebase`, not `analyze_project` alone.

## Traps

| Symptom | What to do |
|---------|------------|
| `edit_workflow_file` twice on the same file in parallel | Serialize writes to one file. |
| MCP cannot edit `project.json` | Punch-list in Studio. |
| Host timeout / JSON-RPC `-32603` | Retry once. Do not send the identical payload three times; change flags (`detail`, `page`, `build:false`) or split the call. |
| Read of a credential file is masked | Keep the mask. Never write the redacted body back. |
| `create_implementation_plan` on an existing 20+ task plan | `overwrite: true` wipes it. Use `update_plan_task`. |

## validate_project flags

The agent green gate is `validate=true`, `build=false`, `pack=false` (typically 24–91s, 0/0). The tool default for `build` remains `true` for callers that omit the flag — **always pass `build: false` in this loop**.

## Prompt

Clients that support MCP Prompts can load `implement_uipath_goal` with `projectPath` and `goal`. It encodes the loop above.

## Resources

URI templates (MCP resources):

- `uipath://skills/{name}`
- `uipath://project/{projectPath}/model`
- `uipath://project/{projectPath}/plan`
- `uipath://project/{projectPath}/workflow/{relativePath}`

`projectPath` and `relativePath` must be percent-encoded, including forward slashes. A raw `C:/...` URI fails.

Worked example for a Windows project root:

`uipath://project/C%3A%2FUsers%2Farauj%2FDocuments%2Fuipath%2Fperf/model`
