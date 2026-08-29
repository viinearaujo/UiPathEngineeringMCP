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

Do **not** close a task with `verify_work` until its `build` flag defaults to false (Task 10). `verify_work` currently forces CLI **build** and can mark a healthy task `blocked`.

File truth is `read_workflow_file` / `search_codebase`, not `analyze_project` alone.

## Traps

| Symptom | What to do |
|---------|------------|
| `verify_work` flips `done` work to `blocked` | Use `validate_project(build:false)` + `update_plan_task`. File-existence checks on `verify_work` are fine; the BUILD step is not. |
| `analyze_project` is huge / truncated by the host | Call with default `detail=summary`. Use `workflowFile` or `detail=full` + `page`. |
| `analyze_project` lists a path that is gone | Rebuild after Task 5; otherwise trust `read_workflow_file`. |
| `manage_workflow_data` add-argument / `add_xaml_workflow` — Studio will not open | Property must live under `x:Members` (Task 11). Do not keep adding root-level `x:Property`. |
| `find_activity` crashes on Main.xaml | Use `edit_workflow_activity` by DisplayName until Task 12. |
| `write_workflow_file` reports bytes but wrong body | Re-read with `read_workflow_file` (Task 13 adds a hash). |
| `get_compile_errors` CS0103 flood | Read `analysisMode`. `partial` / `syntaxOnly` is an artifact baseline, not a full compile. |
| `edit_workflow_file` twice on the same file in parallel | Serialize writes to one file. |
| MCP cannot edit `project.json` | Punch-list in Studio. |
| Read of a credential file is masked | Keep the mask. Never write the redacted body back. |
| Host timeout / JSON-RPC `-32603` | Retry once. Do not send the identical payload three times; change flags (`detail`, `page`, `build:false`) or split the call. |
| `create_implementation_plan` on an existing 20+ task plan | `overwrite: true` wipes it. Use `update_plan_task`. |

## validate_project flags

The agent green gate is `validate=true`, `build=false`, `pack=false` (typically 24–91s, 0/0). The tool default for `build` remains `true` for callers that omit the flag — **always pass `build: false` in this loop**.

## Prompt

Clients that support MCP Prompts can load `implement_uipath_goal` with `projectPath` and `goal`. It encodes the loop above.
