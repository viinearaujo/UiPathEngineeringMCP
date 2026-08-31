# Connecting to UiPath Engineering MCP

The server is passive. The client drives the loop. UiPath facts come from tools; the harness owns retries and sequencing.

## HTTP (Copilot, Inspector, Dev Tunnel)

1. `dotnet run --project src/UiPath.Engineering.Mcp.Server` — listens on `http://localhost:5000`.
2. MCP Streamable HTTP endpoint: `http://localhost:5000/sse` (path name is historical; this is not legacy SSE).
3. Health: `GET http://localhost:5000/health` (never authenticated).
4. Copilot registration endpoint is the Dev Tunnel URL plus `/sse`. See README §4–§5.
5. Optional `/sse` auth: `McpServer:HttpAuth:Enabled` + `ApiKey`. Send `X-Api-Key` or `Authorization: Bearer`.

## stdio (local agents)

```text
dotnet run --project src/UiPath.Engineering.Mcp.Server -- --stdio
```

Logs go to stderr. stdin/stdout are the MCP stream. Spawn this process from the agent host; do not also bind port 5000 in that process.

## Plan path (P0)

Tools resolve **only** `docs/implementation-plan.json` inside the target UiPath project (`ImplementationPlanStore`). Do not move it. `create_implementation_plan` overwrites that file when `overwrite: true` — on a mature plan, call `get_implementation_plan` / `update_plan_task` instead.

## Copilot Studio (RPA default tool set)

This MCP is RPA (`.xaml` / `.cs`) only. Agent instructions (source of truth for the loop): [copilot-studio-agent-instructions.txt](copilot-studio-agent-instructions.txt).

Enable these tools on the default Copilot connector (≤12):

- `analyze_project`, `search_codebase`, `read_workflow_file`
- `validate_project` (always pass `build: false`, `pack: false` in the loop)
- `get_implementation_plan`, `update_plan_task`
- `add_coded_workflow`, `edit_workflow_file`, `get_compile_errors`
- Thin XAML pair: `find_activity`, `insert_activities` (REFramework / InvokeWorkflowFile only)

HTTP `McpServer:ToolSurface` defaults to `CopilotDefault` and advertises only those names. Set `All` for Inspector. GitLab tools stay on the server.

Leave off the default connector: full C# Roslyn suite except `get_compile_errors`, `compile_project`, `verify_work`, `run_ui_path_cli`, `create_implementation_plan`, `generate_documentation`, `write_workflow_file`, `recommend_activities`, `validate_activity_spec`, `build_workflow`, `manage_workflow_data`, GitLab (`search_repository`, `create_work_items`), `list_skills`, `read_skill`.

Do not expect Maestro, IXP, Insights, or Agents playbooks from `list_skills`.

## Safe authoring loop

```text
analyze_project (detail=summary)
  → get_implementation_plan (continue if none exists)
  → add_coded_workflow / edit_workflow_file  (or find_activity + insert_activities for REFramework/Invoke)
  → search_codebase / read_workflow_file to confirm the write
  → validate_project(build:false, pack:false)
  → update_plan_task(done|blocked)
```

Close tasks with `validate_project(build:false, pack:false)` then `update_plan_task`. Marking `done` is not blocked on docs/ADR freshness. `verify_work` still refuses auto-done on docs errors and is not the green gate.

File truth is `read_workflow_file` / `search_codebase`, not `analyze_project` alone.

## HTTP auth

`GET /health` is always anonymous. For non-local Copilot, set `McpServer:HttpAuth:Enabled` true and `McpServer:HttpAuth:ApiKey` (env `McpServer__HttpAuth__ApiKey`). Send `X-Api-Key` or `Authorization: Bearer <key>` on `/sse`. Local/dev can leave auth disabled.

## Traps

| Symptom | What to do |
|---------|------------|
| `edit_workflow_file` twice on the same file in parallel | Serialize writes to one file. |
| `project.json` change needed | `patch_project_json`. Do not emit a patch or overwrite the file. |
| Host timeout / JSON-RPC `-32603` | Retry once. Do not send the identical payload three times; change flags (`detail`, `page`, `build:false`) or split the call. |
| Read of a credential file is masked | Keep the mask. Never write the redacted body back. |
| `update_plan_task(done)` after validate | Plan scratchpad only. Docs/ADR freshness does not block `done`. |
| `create_implementation_plan` on an existing 20+ task plan | `overwrite: true` wipes it. Use `update_plan_task`. |

## validate_project flags

The agent green gate is `validate=true`, `build=false`, `pack=false` (typically 24–91s, 0/0). The tool default for `build` remains `true` for callers that omit the flag — **always pass `build: false` in this loop**.

## Prompt

Clients that support MCP Prompts can load `implement_uipath_goal` with `projectPath` and `goal`. It is a thin recipe of the Copilot agent instructions.

## Resources

URI templates (MCP resources):

- `uipath://skills/{name}`
- `uipath://project/{projectPath}/model`
- `uipath://project/{projectPath}/plan`
- `uipath://project/{projectPath}/workflow/{relativePath}`
- `uipath://project/{projectPath}/knowledge`

`projectPath` and `relativePath` must be percent-encoded, including forward slashes. A raw `C:/...` URI fails.

Worked example for a Windows project root:

`uipath://project/C%3A%2FUsers%2Farauj%2FDocuments%2Fuipath%2Fperf/model`
