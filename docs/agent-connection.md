# Connecting to UiPath Engineering MCP

The server is passive. The client drives the loop. UiPath facts come from tools; the harness owns retries and sequencing.

## HTTP (Copilot, Inspector, Dev Tunnel)

1. `dotnet run --project src/UiPath.Engineering.Mcp.Server` — listens on `http://localhost:5000`.
2. MCP Streamable HTTP endpoint: `http://localhost:5000/sse` (path name is historical; this is not legacy SSE).
3. Health: `GET http://localhost:5000/health` (never authenticated).
4. Copilot registration endpoint is the Dev Tunnel URL plus `/sse`. See README [Microsoft 365 Copilot](../README.md#microsoft-365-copilot) and [Open with Dev Tunnel](../README.md#open-with-dev-tunnel).
5. `/sse` auth: non-Development HTTP requires `McpServer:HttpAuth:Enabled` and a non-empty `ApiKey` at startup. Send `X-Api-Key` or `Authorization: Bearer`. Stdio is unauthenticated.

## stdio (local agents)

```text
dotnet run --project src/UiPath.Engineering.Mcp.Server -- --stdio
```

Logs go to stderr. stdin/stdout are the MCP stream. Spawn this process from the agent host; do not also bind port 5000 in that process.

## Plan path (P0)

Tools resolve **only** `docs/implementation-plan.json` inside the target UiPath project (`ImplementationPlanStore`). Do not move it. `create_implementation_plan` overwrites that file when `overwrite: true` — on a mature plan, call `get_implementation_plan` / `update_plan_task` instead.

## Copilot Studio (RPA default tool set)

This MCP is RPA (`.xaml` / `.cs`) only. Agent instructions (source of truth for the loop): [copilot-studio-agent-instructions.txt](copilot-studio-agent-instructions.txt). Uploaded Copilot Studio skills plus `CopilotConnectorTools.DefaultNames` are the Copilot path.

Enable `CopilotConnectorTools.DefaultNames` on the default Copilot connector (skills, coded intelligence, XAML spec path, plan/scaffold, and project docs — including `analyze_project_gaps`). Canonical list: README recommended-tools line and the DEFAULT CONNECTOR line in [copilot-studio-agent-instructions.txt](copilot-studio-agent-instructions.txt) — both must match the C# array. The agent green gate is `validate_project` (`build` defaults to false, `pack: false`), then `analyze_project_gaps`, then `update_plan_task`. XAML shell: `find_activity` + `insert_activities` (REFramework / InvokeWorkflowFile only), or `recommend_activities` → `validate_activity_spec` → `build_workflow` / `insert_activities` + `manage_workflow_data`. `edit_workflow_activity` is a leave-off fragment hatch.

HTTP `McpServer:ToolSurface` defaults to `CopilotDefault` and advertises only those names. Keep work Copilot on `CopilotDefault`. Set `All` for Inspector hatches only. GitLab tools stay registered on the server.

Leave-off names live in `CopilotConnectorTools.LeaveOffNames` (hatches and aliases only): `write_workflow_file` (full-file overwrite on `ToolSurface=All` only), `edit_workflow_activity` (prefer `insert_activities`), `compile_project` → `validate_project(build:true)`, `verify_work` → `validate_project` then `update_plan_task`, `run_ui_path_cli`, `list_skills` (uploaded skills already name the playbooks), GitLab (`search_repository`, `create_work_items`). Do not leave `analyze_project_gaps` off the default connector.

Do not expect Maestro, IXP, Insights, or Agents playbooks from `list_skills`.

## Discoverability: `.local`, activity docs, and idioms

`.local` is on the filesystem provider's ignore lists, so it never appears in `search_codebase` or the folder tree and generated code cannot leak into the project model or the authoring surfaces. It is still readable by an exact path (`.local/...` through `read_workflow_file`), and two tools read it directly:

- `CSharpContextBuilder` supplies `.local/.codedworkflows/*.cs` to the Roslyn compilation, so the generated `Descriptors.<App>.<Screen>.<Element>` types resolve.
- `search_activity_docs` indexes `{PROJECT_DIR}/.local/docs/packages` first — those are the docs the project's own installed packages ship, so they match the installed versions — then falls back to the vendored `.agents/skills/uipath-rpa/references/activity-docs` snapshot. It resolves the `.local` path through `PathPolicy.TryResolveProjectRelative`, the same canonicalizing sandbox used everywhere else, so the ignore lists stay intact and discovery is served by the tool instead of by unhiding the folder.

`search_uipath_knowledge` covers the guide corpus plus the activity docs, and `uipath://idioms/{name}` serves the shipped Copilot idiom samples directly, so the prompt pack's manual "copy `docs/copilot-idioms/` into the target project" step is no longer required.

## Safe authoring loop

```text
analyze_project (detail=summary)
  → get_implementation_plan (create_implementation_plan if none exists)
  → add_coded_workflow / edit_workflow_file  (or find_activity + insert_activities for REFramework/Invoke)
  → search_codebase / read_workflow_file to confirm the write
  → validate_project(build:false, pack:false)
  → analyze_project_gaps
  → remediate resilience / observability / structure / boundary (ignore category=docs)
  → update_plan_task(done|blocked)
```

Close tasks with `validate_project(build:false, pack:false)`, then `analyze_project_gaps`, then `update_plan_task`. Remediate `resilience`, `observability`, `structure`, and coded/XAML boundary gaps before `done`. Ignore `category=docs`. Marking `done` is not blocked on docs/ADR freshness. `verify_work` still refuses auto-done on docs errors and is not the green gate.

File truth is `read_workflow_file` / `search_codebase`, not `analyze_project` alone.

## HTTP auth

`GET /health` is always anonymous. Non-Development HTTP requires `McpServer:HttpAuth:Enabled` true and a non-empty `McpServer:HttpAuth:ApiKey` (env `McpServer__HttpAuth__ApiKey`) at startup. Send `X-Api-Key` or `Authorization: Bearer <key>` on `/sse`. Development may leave auth off for localhost; do not expose that configuration through a Dev Tunnel. Stdio is unauthenticated. When Enabled is true and the key is empty, `/sse` is fail-closed.

## Protocol limits over HTTP

`HttpServerTransportOptions.Stateless` defaults to `true` as of the `2026-07-28` protocol revision (SEP-2567), which removed `Mcp-Session-Id` from Streamable HTTP. The server does not override it. Under the current revision, HTTP requests are only served when `Stateless` is `true`; a `false` setting is refused with `-32022 UnsupportedProtocolVersion` so a dual-path client downgrades to the `initialize` handshake. Each HTTP request gets a fresh server context, and a response could otherwise arrive at a different ASP.NET Core process — so **no server-to-client request and no unsolicited server-to-client message is possible over HTTP**.

Do not plan any of these for the HTTP transport:

| Capability | HTTP | stdio |
|------------|------|-------|
| Sampling (`sampling/createMessage`) | unavailable | available |
| Elicitation (`elicitation/create`) | unavailable | available |
| Roots (`roots/list`) | unavailable | available |
| Resource subscriptions (`resources/subscribe`, `resources/updated`) | unavailable | available |
| List-changed notifications (`tools/list_changed`, `resources/list_changed`, `prompts/list_changed`) | unavailable | available |
| Progress notifications (`notifications/progress`) | **available** | available |

Progress is the exception because a progress notification is scoped to the `ProgressToken` of the in-flight request and travels back on that request's own response stream — it is not an unsolicited server-to-client message. It is the only long-call feedback channel the HTTP transport offers. Long CLI-backed calls (`validate_project` with `pack:true`, `create_project`, `build_workflow`) can take tens of seconds, so the client must carry a timeout generous enough for them; without progress notifications there is nothing to poll in the meantime.

Recovering any of the unavailable capabilities requires opting the server out of the current protocol revision, which costs HTTP interoperability with clients on `2026-07-28` and later. The client-driven loop is the design: the server is passive, tools are one-shot request/response, and the harness owns sequencing and retries.

Stdio (`--stdio`) is a separate process with a persistent bidirectional stream, so it is unaffected by all of the above.

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

The agent green gate is `validate=true`, `build=false` (the tool default), `pack=false` (typically 24–91s, 0/0). Pass `build:true` or `compile_project` only for an authoritative CLI compile.

## Prompt

Clients that support MCP Prompts can load `implement_uipath_goal` with `projectPath` and `goal`. It is a thin recipe of the Copilot agent instructions.

## Resources

> ⚠️ **Every resource registered by this server is a URI *template*, so `resources/list` returns an empty array.** A client that only reads `resources/list` sees zero resources. Enumerate them with `resources/listResourceTemplates`, then `resources/read` a concrete URI built by substituting the `{...}` placeholders.
>
> **Verify this against the actual Copilot Studio registration.** If Studio's resource discovery reads only `resources/list`, it currently sees none of the resources below despite them being implemented and reachable by exact URI.

URI templates (MCP resources):

- `uipath://skills/{name}`
- `uipath://project/{projectPath}/model`
- `uipath://project/{projectPath}/plan`
- `uipath://project/{projectPath}/workflow/{relativePath}`
- `uipath://project/{projectPath}/knowledge`
- `uipath://activity/{projectPath}/{name}` — one activity's full authoring surface as JSON. Substitute the literal `global` for `projectPath` to read the built-in fallback catalog.
- `uipath://idioms/{name}` — a shipped Copilot idiom sample (`coded-workflow-try-log.md`, `thin-reframework-invoke.md`, `coded-testcase.md`). Read it over the resource instead of copying the sample into the target project.

`projectPath` and `relativePath` must be percent-encoded, including forward slashes. A raw `C:/...` URI fails. The `uipath://activity/...` template has no project requirement: `global` selects the built-in catalog, and any other value must be an allowed project directory.

Worked example for a Windows project root:

`uipath://project/C%3A%2FUsers%2Farauj%2FDocuments%2Fuipath%2Fperf/model`

## Copilot prompt pack

Copy-paste user messages (new feature, change existing, debug, update project documentation) live in [copilot-prompts.md](copilot-prompts.md). Each template carries `{PROJECT_PATH}`, `{GOAL}`, optional `{PLAN_MD}`, and optional `{IDIOM_DIR}` (default `docs/idioms`).

Idiom samples to copy into a UiPath project’s `docs/idioms/` are in [copilot-idioms/](copilot-idioms/). Copilot grounds on those local paths with `read_workflow_file` — not SharePoint or Dataverse. The files must sit inside an allowed project (`project.json` + `Projects:AllowedRoots`).
