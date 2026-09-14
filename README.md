# UiPath Engineering MCP

![.NET 8](https://img.shields.io/badge/.NET-8-512BD4?logo=dotnet&logoColor=white)
![MCP](https://img.shields.io/badge/MCP-Model%20Context%20Protocol-555)
![Windows](https://img.shields.io/badge/Windows-0078D4?logo=windows&logoColor=white)
![RPA](https://img.shields.io/badge/RPA-UiPath-FA4616)

A custom **.NET 8** Model Context Protocol (MCP) server that lets an AI client
(Microsoft 365 Copilot, MCP Inspector, Claude, and friends) analyze and validate
UiPath RPA projects over HTTP, reached from the outside world with **Microsoft Dev Tunnel**.

This is the **MVP / POC (v4)** milestone. Tool names and the Copilot default set are listed under Toolkit; do not hard-code a count.
The skills feed under `.agents/skills` is **RPA-only** — do not reinstall the full UiPath marketplace catalog.

**What you can do tonight**

- 🔍 **Analyze a UiPath project** — parse `project.json`, workflows, coded files, risks, and the invoke graph
- 💻 **Author coded workflows** — add `.cs` workflows and tests, edit them, and check compile errors in memory
- ✅ **Validate and close the plan loop** — green gate, gap analysis, then mark the task done

**Contents**

- [🏡 What this is](#what-this-is)
- [⚡ Quick start](#quick-start)
- [🧰 Prerequisites](#prerequisites)
- [🗂️ Allow your project folders](#allow-your-project-folders)
- [🖥️ Run locally](#run-locally)
- [🌐 Open with Dev Tunnel](#open-with-dev-tunnel)
- [🤝 Microsoft 365 Copilot](#microsoft-365-copilot)
- [✨ Coded-first authoring](#coded-first-authoring)
- [🔁 Autonomous loop](#autonomous-loop)
- [🛠️ Toolkit](#toolkit)
- [🧪 Tests](#tests)
- [📁 Project layout](#project-layout)
- [📌 Notes and limits](#notes-and-limits)
- [📚 Further reading](#further-reading)

---

<a id="what-this-is"></a>
## 🏡 What this is

A workshop for agents that work on **UiPath RPA** (`.xaml` / `.cs`) — not a full UiPath product catalog.

- HTTP on `http://localhost:5000` (`/health`, `/sse`) **or** local stdio (`--stdio`) — one process, one transport
- Default HTTP (`McpServer:ToolSurface=CopilotDefault`) advertises `CopilotConnectorTools.DefaultNames`
- Inspector / `McpServer:ToolSurface=All` advertises the full live catalog of every registered `[McpServerTool]` (same tools the grouped Toolkit already describes). `LeaveOffNames` is hatches and aliases hidden on the Copilot default connector, not the runtime source for All
- New work is **coded-first** unless the task is REFramework or orchestration XAML

<a id="quick-start"></a>
## ⚡ Quick start

```powershell
./scripts/run-local.ps1            # add -SkipTests to skip the test run
```

That restores, builds, tests, runs, and checks `/health`.

```powershell
# Health -> should return 200 "Healthy"
Invoke-WebRequest http://localhost:5000/health
```

Point MCP Inspector at `http://localhost:5000/sse` (transport: **Streamable HTTP**):

```powershell
npx @modelcontextprotocol/inspector
```

> 💡 **Tip:** On the default HTTP surface you should see the Copilot default catalog (`analyze_project`, `search_codebase`, `read_workflow_file`, skills, spec, docs, and friends). Set `McpServer:ToolSurface` to `All` for Inspector hatches. Local stdio recipes live in [docs/agent-connection.md](docs/agent-connection.md).

<a id="prerequisites"></a>
## 🧰 Prerequisites

- Windows
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- UiPath CLI (`uip`) on `PATH` (only required for `validate_project`): `npm install -g @uipath/cli`.
  The npm install puts `uip.cmd` / `uip.ps1` shims on `PATH` (no `uip.exe`); the server
  probes `PATH` for `uip.exe` → `uip.cmd` → `uip.bat` → `uip.ps1` and launches script
  shims through `cmd.exe` / `powershell.exe` automatically.
- UiPath CLI RPA tool for `create_project`: `uip tools install @uipath/rpa-tool`
  (the file-authoring tools `add_xaml_workflow`, `write_workflow_file`, `add_coded_workflow`,
  `edit_workflow_activity` work without any CLI)
- [Microsoft Dev Tunnel CLI](https://learn.microsoft.com/azure/developer/dev-tunnels/get-started) (`devtunnel`)

Check your toolchain:

```powershell
dotnet --version      # expect 8.x
uip --version         # UiPath CLI
devtunnel --version
```

<a id="allow-your-project-folders"></a>
## 🗂️ Allow your project folders

Committed `appsettings.json` ships `Projects:AllowedRoots` as `[]`. Set the folders that
contain your UiPath projects locally (`appsettings.Development.json`, user secrets, or
`Projects__AllowedRoots__0`). Only paths **inside** these roots can be analyzed or
validated (security guard). Do not commit personal machine paths. Example:

```json
"Projects": {
  "AllowedRoots": [
    "C:/path/to/your/uipath-projects"
  ]
}
```

> 🔒 **Security:** empty committed roots are intentional. Writes and analysis stay inside `AllowedRoots`.

`UiPathCli:IncludeRawOutput` is `false` by default (raw CLI console output is suppressed).
`appsettings.Development.json` turns it on for local debugging.

<a id="run-locally"></a>
## 🖥️ Run locally

Quickest path is [⚡ Quick start](#quick-start). Manual path:

```powershell
cd UiPathEngineeringMCP
dotnet restore
dotnet build
dotnet run --project src/UiPath.Engineering.Mcp.Server
```

```powershell
# Local agents (stdio; logs on stderr)
dotnet run --project src/UiPath.Engineering.Mcp.Server -- --stdio
```

HTTP listens on `http://localhost:5000`. MCP (Streamable HTTP) is at `/sse`. `--stdio` is already shipped as a separate process: logs on stderr, does not bind port 5000, unauthenticated, full tool surface (including `LeaveOffNames` hatches). One process, one transport — do not run HTTP and `--stdio` together.

<a id="open-with-dev-tunnel"></a>
## 🌐 Open with Dev Tunnel

Quickest path — enable HTTP auth (`McpServer:HttpAuth:Enabled` and a non-empty `ApiKey`),
then run the helper (idempotent create + host, prints the public URLs). A Development
host may start with auth off for localhost; do not tunnel that configuration.

```powershell
./scripts/setup-devtunnel.ps1
```

Or do it manually:

```powershell
devtunnel user login
devtunnel create uipath-mcp
devtunnel port create uipath-mcp --port 5000 --protocol http
devtunnel host uipath-mcp
```

Dev Tunnel prints a public HTTPS URL, e.g. `https://<id>-5000.devtunnels.ms`.
Keep `dotnet run` going in one terminal and `devtunnel host` in another.

Verify through the tunnel:

```powershell
Invoke-WebRequest https://<id>-5000.devtunnels.ms/health
```

Your MCP endpoint for clients is: `https://<id>-5000.devtunnels.ms/sse`

> ⚠️ **Warning:** Non-Development HTTP refuses to start unless `McpServer:HttpAuth:Enabled` is true and
> `McpServer:HttpAuth:ApiKey` is non-empty (env `McpServer__HttpAuth__ApiKey`). Development
> may leave auth off for localhost. Stdio (`--stdio`) is unauthenticated. Copilot must send
> `X-Api-Key: <key>` (header name overridable via `HttpAuth:HeaderName`) or
> `Authorization: Bearer <key>`. `GET /health` is never authenticated. When Enabled is true
> and the key is empty, `/sse` is fail-closed (401).

<a id="microsoft-365-copilot"></a>
## 🤝 Microsoft 365 Copilot

- **Name:** UiPath Engineering MCP
- **Endpoint:** `https://<id>-5000.devtunnels.ms/sse`
- **Agent instructions:** paste [docs/copilot-studio-agent-instructions.txt](docs/copilot-studio-agent-instructions.txt) (source of truth for the Copilot loop). Uploaded Copilot Studio skills plus this catalog are the Copilot path. New work is coded unless it is REFramework/orchestration. XAML may invoke coded workflows with primitives only; never custom types or source-file methods from XAML.
- **Recommended tools (default connector):** `analyze_project`, `search_codebase`, `read_workflow_file`, `validate_project`, `get_implementation_plan`, `update_plan_task`, `add_coded_workflow`, `edit_workflow_file`, `find_activity`, `insert_activities`, `get_compile_errors`, `analyze_project_gaps`, `read_skill`, `explain_workflow`, `get_workflow_dependencies`, `generate_documentation`, `find_code_symbol`, `find_code_references`, `get_code_context`, `validate_activity_spec`, `recommend_activities`, `build_workflow`, `manage_workflow_data`, `add_xaml_workflow`, `create_implementation_plan`, `create_project`, `patch_project_json`, `manage_project_docs`, `manage_project_file`, `sync_project_context`, `validate_project_docs`
- **Leave off the default connector:** `CopilotConnectorTools.LeaveOffNames` (hatches and aliases only). Notable overlaps: `compile_project` (use `validate_project(build:true)`), `verify_work` (use `validate_project` then `update_plan_task`), `edit_workflow_activity` (prefer `insert_activities`; fragment hatch on `All`), `write_workflow_file` (full-file overwrite on `ToolSurface=All` only), `list_skills` (uploaded skills already name the playbooks). HTTP `McpServer:ToolSurface` defaults to `CopilotDefault` and advertises only `CopilotConnectorTools.DefaultNames`. Keep work Copilot on `CopilotDefault`. Set `All` for Inspector. GitLab tools stay registered on the server.
- **Full tool surface** (Inspector / `ToolSurface=All`): the live catalog of every registered `[McpServerTool]` — the same tools the grouped Toolkit already describes. `LeaveOffNames` is not the runtime source for All; it is the hatch/alias set hidden on the Copilot default connector.

> ✅ **Green gate:** `validate_project(build:false, pack:false)` → `analyze_project_gaps` → `update_plan_task`. Do not use `verify_work` as the done gate.

This server is **RPA only** (`.xaml` / `.cs`). Do not install the full `uip skills` marketplace catalog into `.agents/skills`.

<a id="coded-first-authoring"></a>
## ✨ Coded-first authoring

New work is a **coded workflow**, **coded test case**, or **coded source file** unless
the task is REFramework or orchestration XAML. Create files with `add_coded_workflow`
(`kind`: `workflow` / `test` / `source`); edit `.cs` with `edit_workflow_file` after
`read_workflow_file`; use `get_compile_errors` for a fast in-memory `.cs` check.

`kind=test` uses a `[TestCase]` template and registers `designOptions.fileInfoCollection`
— never `entryPoints`. `kind=workflow` registers `entryPoints`. `kind=source` is a plain
helper class and is not registered.

XAML is a thin shell: `find_activity` + `insert_activities` for REFramework and
`InvokeWorkflowFile` wiring only. XAML may invoke coded workflows with primitives only;
never custom types or source-file methods from XAML.

Spec-based XAML authoring (`validate_activity_spec`, `build_workflow`, `manage_workflow_data`)
is on the Copilot default connector for that shell. Spec shape:
`{ name, properties, children, variables (root only), catches (TryCatch only), else (If), cases/default (Switch), arguments (InvokeWorkflowFile) }`.

Example spec (Invoke of a coded workflow with primitive args):

```json
{
  "name": "Sequence",
  "children": [
    {
      "name": "InvokeWorkflowFile",
      "properties": { "workflowFileName": "InvoiceFlow.cs" },
      "arguments": [
        { "name": "in_CustomerId", "direction": "In", "type": "String", "value": "[in_CustomerId]" }
      ]
    }
  ]
}
```

Strings enclosed in square brackets ([expr]) are interpreted as expressions in the project's configured expression language. All other values are treated as literals.

<a id="autonomous-loop"></a>
## 🔁 Autonomous loop

The server answers one deterministic tool call at a time. The client drives the sequence:

```
analyze_project (summary) → get_implementation_plan
   → add_coded_workflow / edit_workflow_file  (or find_activity + insert_activities for REFramework/Invoke)
   → get_compile_errors (optional .cs check)
   → validate_project(build:false, pack:false) → analyze_project_gaps
   → remediate resilience / observability / structure / boundary (ignore category=docs)
   → update_plan_task
```

Example prompt:

> Analyze my UiPath project (summary), resume the existing implementation plan if present,
> implement the next pending task, validate with `validate_project` build false pack false,
> run `analyze_project_gaps` and remediate resilience/boundary gaps (ignore category=docs),
> then mark the task done with `update_plan_task`.

Plans live at `docs/implementation-plan.json` (scratchpad). Marking a task `done` is not
blocked on docs or ADR freshness. Do not use `verify_work` as the done gate. It defaults
`build: false` and does not auto-block on a build-only failure; the green gate is still
`validate_project(build:false, pack:false)` then `analyze_project_gaps` then
`update_plan_task`. Connection recipes and traps:
[docs/agent-connection.md](docs/agent-connection.md).

<a id="toolkit"></a>
## 🛠️ Toolkit

Grouped by job. Copilot's default catalog is listed first; Inspector / `ToolSurface=All`
adds hatches and aliases (`LeaveOffNames`).

### ⭐ Copilot default

- `analyze_project` — Parses `project.json` + workflows/coded files into a cached model; default response is a summary (counts, workflow index, coded-file kind, packages, risks, folder tree). Pass `detail='full'` to page complete workflow models, or `workflowFile` for one workflow.
- `search_codebase` — Substring search across `.xaml` and `.cs` in `text`, `symbol`, `activity`, or `workflow` mode. Exact-case first; capped at 200 matches with a `truncated` flag.
- `read_workflow_file` — Reads any project text file with line numbers and pagination (`startLine`/`lineCount`, default 1000). Secret values are redacted; `.env` / `*.pem` / `*.key` are refused.
- `validate_project` — Runs `uip rpa validate` / `build` / `pack` (`--output json`) with per-step `executed`/`success`/`errors`/`warnings`. Agent green gate: `build:false`, `pack:false`. Authoritative CLI compile: `build:true`.
- `get_implementation_plan` — Returns the project's implementation plan with derived per-status task counts.
- `update_plan_task` — Updates one task (`pending` / `in_progress` / `done` / `blocked`) and optional notes. `done` is not blocked on docs/ADR freshness.
- `add_coded_workflow` — Adds a coded workflow (`kind=workflow`, `[Workflow]`, `entryPoints`), coded test (`kind=test`, `[TestCase]`, `fileInfoCollection` — never `entryPoints`), or plain source (`kind=source`). Optional `relativeFolder`; Process + `kind=test` defaults to `Tests\`.
- `edit_workflow_file` — Exact string replace in a `.xaml`/`.cs` file; fails on zero or ambiguous matches unless `replaceAll: true`.
- `find_activity` — Finds activities in `.xaml` and returns stable per-snapshot ids, line numbers, and ancestor chains. Pass the returned `id` to `insert_activities`.
- `insert_activities` — Recommended Copilot surgical XAML path: inserts a JSON spec as children of the activity located by `activityId` or `DisplayName`.
- `get_compile_errors` — Structured Roslyn diagnostics (file/line/column/code/severity/message) without a build; responses include `analysisMode` (`full` / `partial` / `syntaxOnly`).
- `analyze_project_gaps` — Deterministic hygiene gaps (entry point, orphans, exception handling, logging, descriptions, tests, unresolved invokes, coded/XAML primitive-only invoke boundary) plus plan cross-checks; each gap names the MCP tool that fixes it.
- `read_skill` — Reads one RPA skill (`SKILL.md` or an auxiliary file).
- `explain_workflow` — Structured breakdown of one workflow: arguments, variables, activity outline, handlers, invokes, log messages. Coded (`.cs`) files return `kind` (`workflow` / `test` / `source`), class, namespace, entry methods, and public methods.
- `get_workflow_dependencies` — `InvokeWorkflowFile` graph: project-wide edges, cycles, orphans, unresolved targets; or, with `workflowFile`, that workflow's callers/callees with argument mappings.
- `generate_documentation` — Deterministic structured docs for the whole project: metadata, per-workflow summaries, dependency graph (edges, cycles, orphans), risks.
- `find_code_symbol` — Finds C# symbols (methods, classes, properties, fields, interfaces) by exact name via Roslyn; returns kind, file, line, containing type, signature.
- `find_code_references` — All usage sites of a C# symbol across the project's `.cs` files (semantic matching, identifier fallback for external symbols).
- `get_code_context` — Semantic context of one C# member (by symbol name or file+line): signature, containing type, called methods, referenced types, and source.
- `validate_activity_spec` — Dry-run validation of a JSON activity spec against the catalog — no files read or written. Returns structured errors (`errorCode`/`message`/`fixHint`) or the catalog activities used.
- `recommend_activities` — Up to 5 version-aware activity schemas for a natural-language query (`uip rpa activities find` when available; otherwise the built-in fallback). Call before `validate_activity_spec` when the type is unknown.
- `build_workflow` — Creates a real `.xaml` from a JSON spec (run `validate_activity_spec` first). Never overwrites unless `overwrite: true`.
- `manage_workflow_data` — Add, remove, or rename arguments (`x:Property`) and variables (`Sequence.Variables`) on an existing `.xaml`.
- `add_xaml_workflow` — Adds a blank `.xaml` with the correct `x:Class` naming (relative path, separators → underscores).
- `create_implementation_plan` — Creates a plan from a goal + ordered tasks; writes `docs/implementation-plan.json` plus a Markdown mirror. Refuses to overwrite unless `overwrite: true`.
- `create_project` — Scaffolds a new UiPath project via `uip rpa init` (requires the UiPath CLI RPA tool). Detects the documented partial-success case by checking the created files.
- `patch_project_json` — One structured `project.json` operation (entry points, dependencies, fileInfoCollection, exception handler, runtimeOptions). Never changes `expressionLanguage`, `targetFramework`, or `schemaVersion`.
- `manage_project_docs` — Lists, writes, deletes, or keyword-searches knowledge articles and ADRs (`kind`: memory / adr / context / all).
- `manage_project_file` — Creates, edits, or deletes a `.md` / `.json` / `.txt` file. Refuses `project.json`, plan files, `docs/knowledge`, `docs/adr`, secret names, and `***REDACTED***` bodies.
- `sync_project_context` — Regenerates `AGENTS.md` (marker block) and `.claude/rules/project-context.md` from the project model.
- `validate_project_docs` — Inspects docs without changing plan state. Wiki hygiene only — does not block `update_plan_task(done)`. `verify_work` still refuses auto-done on docs errors.

### 🔍 Understand

- `analyze_project` — See Copilot default.
- `explain_workflow` — See Copilot default.
- `search_codebase` — See Copilot default.
- `read_workflow_file` — See Copilot default.
- `get_workflow_dependencies` — See Copilot default.
- `generate_documentation` — See Copilot default.

### 💻 Author (coded)

- `add_coded_workflow` — See Copilot default.
- `edit_workflow_file` — See Copilot default.
- `get_compile_errors` — See Copilot default.
- `find_code_symbol` — See Copilot default.
- `find_code_references` — See Copilot default.
- `get_code_context` — See Copilot default.

### 🧩 Author (XAML shell)

- `find_activity` — See Copilot default.
- `insert_activities` — See Copilot default.
- `validate_activity_spec` — See Copilot default.
- `recommend_activities` — See Copilot default.
- `build_workflow` — See Copilot default.
- `manage_workflow_data` — See Copilot default.
- `add_xaml_workflow` — See Copilot default.
- `write_workflow_file` — Leave-off full-file overwrite (`ToolSurface=All` only). Prefer `edit_workflow_file` for small edits and `insert_activities` for spec inserts. For `.xaml`, activity types must be in the project catalog unless `allowUnknownActivities` is true.
- `edit_workflow_activity` — Leave-off XAML fragment hatch (`ToolSurface=All` only). Prefer `insert_activities`. Inserts a raw fragment, or replaces/removes one activity, by `activityId` or `DisplayName`.

### ✅ Plan and close

- `create_implementation_plan` — See Copilot default.
- `get_implementation_plan` — See Copilot default.
- `update_plan_task` — See Copilot default.
- `analyze_project_gaps` — See Copilot default.
- `validate_project` — See Copilot default. Green gate: `build:false`, `pack:false`.
- `verify_work` — Leave-off bundled check (not on the Copilot default connector). Prefer `validate_project(build:false, pack:false)` then `update_plan_task`. Rebuilds the model, runs CLI validate (optional `build`), checks expected files, and can mark plan tasks `done` or `blocked`. Not the agent done gate.
- `compile_project` — Leave-off CLI build (not on the Copilot default connector). Prefer `validate_project(build:true)`. Do not use as the agent-loop green gate.

### 📝 Project files and docs

- `manage_project_file` — See Copilot default.
- `patch_project_json` — See Copilot default.
- `manage_project_docs` — See Copilot default.
- `sync_project_context` — See Copilot default.
- `validate_project_docs` — See Copilot default.

### 🦊 GitLab, CLI, skills

- `search_repository` — Leave-off GitLab hatch (`ToolSurface=All` only). Searches GitLab issues for the configured project (requires the `GitLab` config section; token is never returned).
- `create_work_items` — Leave-off GitLab hatch (`ToolSurface=All` only). Creates GitLab issues/work items from `{ title, description, labels? }`, returning created IDs/URLs and per-item failures.
- `create_project` — See Copilot default.
- `run_ui_path_cli` — Leave-off unbounded CLI hatch (`ToolSurface=All` only). Runs an allowlisted `uip` command (default verbs: `rpa`, `solution`); mutating subcommands are blocked unless enabled in config, shell metacharacters are rejected, and stdout/stderr are redacted and capped.
- `list_skills` — Leave-off (`ToolSurface=All` only). Lists RPA playbooks only (`uipath-rpa`, `guided-implementation-loop`). Not a full UiPath product catalog. Uploaded Copilot Studio skills already name the playbooks; use `read_skill` on the default connector for package-local refs.
- `read_skill` — See Copilot default.

<a id="tests"></a>
## 🧪 Tests

```powershell
dotnet test
```

The `tests/` folder contains four xUnit projects. Tests use hand-written fakes (no Moq).

| Project | Covers |
|---------|--------|
| `UiPath.Engineering.Mcp.Core.Tests` | `project.json` parsing, `ProjectModelBuilder` (xaml + coded `.cs`), `XamlWorkflowParser`, `CodedSourceFileParser`, `DependencyGraphBuilder`, XAML/C# templates, `ImplementationPlanStore`, `ProjectGapAnalyzer`, project file policy, JSON patcher, knowledge/ADR stores, context renderer, docs validator and search. |
| `UiPath.Engineering.Mcp.Providers.Tests` | Path allow-listing, filesystem write/delete guards, `.xaml`/`.cs` discovery skipping `bin`/`obj`/`.git`, `GetDirectoryTree`, `CliExecutableResolver`, `UiPathCliOutputParser`, `UiPathCliProvider` per-step results, `GitStatusParser`, `GitLabProvider` (token never surfaced). |
| `UiPath.Engineering.Mcp.Tools.Tests` | Project-docs tools, path/project guards, validate output shape, authoring guards, coded-workflow entry-point registration, `uip rpa init` partial-success, activity-level and spec-based authoring, `read_workflow_file` / `edit_workflow_file`, plan create/update/get (`update_plan_task(done)` is not docs-gated), gap-analysis shape, `verify_work` branches, C# analysis tools, `search_codebase`, structured errors. |
| `UiPath.Engineering.Mcp.Server.Tests` | HTTP host, Copilot default connector list, `/sse` API-key auth (on/off, fail-closed empty key, `/health` anonymous), non-Development HTTP startup validation, empty committed `AllowedRoots`, and `update_plan_task(done)` not blocked on docs freshness. |

<a id="project-layout"></a>
## 📁 Project layout

```
src/
  UiPath.Engineering.Mcp.Server/     # ASP.NET host: DI, config, /health, /sse (MapMcp)
  UiPath.Engineering.Mcp.Core/       # Models, config options, project.json + XAML parsing, dependency graph
  UiPath.Engineering.Mcp.Providers/  # Filesystem + UiPath CLI providers (structured CLI output parser)
  UiPath.Engineering.Mcp.Tools/      # [McpServerTool] classes (analyze/validate/explain/document/author)
```

<a id="notes-and-limits"></a>
## 📌 Notes and limits

**Disk writes.** Authoring tools (`create_project`, `add_xaml_workflow`, `write_workflow_file`,
`add_coded_workflow`, `edit_workflow_activity`, `build_workflow`, `insert_activities`,
`manage_workflow_data`) write to disk. They are restricted to `Projects:AllowedRoots`,
reject path escapes outside the target project, and only accept `.xaml`/`.cs` content.
`create_project` delegates scaffolding to `uip rpa init` (files are never hand-written);
`uip rpa init`'s documented partial-success case is detected by checking the created files.

**Coded / XAML boundary.** Coded-first authoring (`add_coded_workflow`, `edit_workflow_file`,
`get_compile_errors`) is the default for new work. Spec-based XAML (`validate_activity_spec`,
`build_workflow`, `insert_activities`, `manage_workflow_data`) is the thin REFramework/orchestration
shell. `edit_workflow_activity` fragment mode remains an escape hatch for edits the spec model
does not cover. In the spec model an `If` activity's `children` are the **Then** branch only.
`manage_workflow_data` rename updates the declaration only; expressions referencing the old name
are not rewritten. XAML `InvokeWorkflowFile` of a `.cs` workflow may pass only primitives,
`DataTable`, or `string[]`; coded source methods must never be called from XAML.

**Activity targeting.** `edit_workflow_activity` matches by `DisplayName` (exact, case-sensitive);
when several activities share a name the edit is rejected and `activityType` must be passed
to disambiguate. Inserted fragments are re-indented to match the container; the rest of the
file is preserved byte-for-byte.

**Coded registration.** `add_coded_workflow` registers coded workflows in `project.json`
`entryPoints` with a generated GUID; coded test cases (`kind=test`) go in
`designOptions.fileInfoCollection` and never in `entryPoints`; plain source files are not registered.

**Cache.** `analyze_project` caches a full project model (fingerprint: SHA-256 of sorted path +
last-write ticks for `project.json`, `*.xaml`, and `*.cs` — renames invalidate the cache even
when timestamps are preserved) but the default MCP response is a **summary** without activity
trees. Pass `detail='full'` to page complete workflow models (`page`/`pageSize`), or `workflowFile`
to load one workflow fully. Cycles and orphan workflows are flagged as risks.

**CLI.** `validate_project` requires the UiPath CLI (`uip`, npm `@uipath/cli`); the executable
is resolved on PATH among `uip.exe`/`uip.cmd`/`uip.bat`/`uip.ps1` (script shims are launched
via `cmd.exe`/`powershell.exe`). On non-Windows / missing CLI it returns a structured error
instead of crashing.

**`/sse` name.** The `/sse` path serves the **Streamable HTTP** transport (not legacy SSE);
the name is kept only to match the Copilot registration docs. HTTP auth (`McpServer:HttpAuth`)
is required at startup outside Development; Development may leave it off. Stdio is
unauthenticated. HTTP Copilot advertises the default connector unless
`McpServer:ToolSurface` is `All`.

**Roslyn `analysisMode`.** The C# analysis tools (`find_code_symbol`, `find_code_references`,
`get_code_context`, `get_compile_errors`) build a cached in-memory Roslyn compilation per
project. When NuGet package assemblies cannot be resolved the response reports
`analysisMode: "partial"` (some references missing — results may be incomplete) or
`"syntaxOnly"` (NuGet folder unreachable — declaration/name matching only), so the client
always knows how much to trust the result.

**Not yet.** Publish/deploy to Orchestrator (`uip solution publish/deploy`) is a separate
future phase. The PowerShell provider is a planned phase, not yet implemented.

<a id="further-reading"></a>
## 📚 Further reading

- [docs/agent-connection.md](docs/agent-connection.md) — stdio, Inspector, and connection traps
- [docs/copilot-prompts.md](docs/copilot-prompts.md) — prompt recipes
- [docs/copilot-studio-agent-instructions.txt](docs/copilot-studio-agent-instructions.txt) — paste into Copilot Studio
- Idioms under [`docs/copilot-idioms/`](docs/copilot-idioms/)
