# UiPath Engineering MCP

A custom **.NET 8** Model Context Protocol (MCP) server that lets an AI client
(Microsoft 365 Copilot, MCP Inspector, Claude, etc.) analyze and validate UiPath
RPA projects over HTTP, exposed to the outside world with **Microsoft Dev Tunnel**.

This is the **MVP / POC (v4)** milestone. Thirty-eight tools are implemented.
The skills feed under `.agents/skills` is **RPA-only** (do not reinstall the full UiPath marketplace catalog).

| Tool | What it does |
|------|--------------|
| `analyze_project` | Parses project.json + workflows/coded files into a cached project model. Default response is a summary (counts, workflow index, packages, risks, folder tree). Pass detail='full' to page complete workflow models (page/pageSize), or workflowFile to load one workflow fully. |
| `validate_project` | Runs the UiPath CLI (`uip rpa validate` / `build` / `pack` with `--output json`) and returns structured per-step results (`executed`/`success`/`errors`/`warnings` each) plus recommendations. |
| `explain_workflow` | Returns the structured breakdown of a single workflow: arguments, variables, activity outline, exception handlers, invoked workflows, log messages. Coded (`.cs`) workflows return class name, namespace, entry methods, and public methods. |
| `generate_documentation` | Returns deterministic structured documentation data for the whole project: metadata, per-workflow summaries, dependency graph (edges, cycles, orphans), risks. |
| `search_repository` | Searches GitLab issues for the configured project (requires the `GitLab` config section; token is never returned). |
| `create_work_items` | Creates GitLab issues/work items from a list of `{ title, description, labels? }`, returning created IDs/URLs and per-item failures. |
| `create_project` | Scaffolds a new UiPath project via `uip rpa init` (requires the UiPath CLI RPA tool installed on the host). Detects the documented partial-success case by checking the created files. |
| `add_xaml_workflow` | Adds a blank `.xaml` workflow to an existing project, with the correct `x:Class` naming (relative path, separators → underscores). |
| `write_workflow_file` | Creates or fully overwrites a `.xaml` or `.cs` file inside a project with caller-supplied content (extension allowlist + path-escape guard). |
| `read_workflow_file` | Reads any text file inside a project with line numbers and pagination (`startLine`/`lineCount`, default 1000 lines); obvious secret values are redacted and `.env`/`*.pem`/`*.key` files are refused. |
| `edit_workflow_file` | Replaces an exact string in a `.xaml`/`.cs` file; fails on zero or ambiguous matches unless `replaceAll: true`. Preferred over `write_workflow_file` for small changes. |
| `find_activity` | Finds activities in `.xaml` workflows and returns stable per-snapshot activity IDs, line numbers, and ancestor chains. Filter by `workflowFile`, DisplayName substring (`query`), exact `activityType`, or exact `activityId`. Pass the returned `id` to `edit_workflow_activity` / `insert_activities`. Unparsable workflows are skipped and reported as warnings. |
| `get_workflow_dependencies` | Shows the `InvokeWorkflowFile` graph: project-wide edges, cycles, orphans, and unresolved targets; or, with `workflowFile`, that workflow's callers/callees with argument mappings. |
| `edit_workflow_activity` | Activity-level XAML editing: insert an activity fragment into a container, replace, or remove an activity located by `activityId` (from `find_activity`) or `DisplayName` (optional `activityType` disambiguation). Whitespace-preserving; fragments understand unprefixed WF activities plus the `ui:`/`x:` prefixes. |
| `validate_activity_spec` | Dry-run validation of a JSON activity spec against the UiPath activity catalog — no files read or written. Returns every violation as a structured error (`errorCode`/`message`/`fixHint`), or the list of catalog activities the spec uses. |
| `build_workflow` | Creates a real `.xaml` workflow file in a project from a JSON activity spec (run `validate_activity_spec` first). Never overwrites an existing file unless `overwrite: true`. |
| `insert_activities` | Inserts activities described by a JSON activity spec into an existing `.xaml` workflow, as children of the activity located by `activityId` (from `find_activity`) or `DisplayName` — the spec-based sibling of `edit_workflow_activity`. |
| `manage_workflow_data` | Manages the data surface of an existing `.xaml` workflow: add, remove, or rename arguments (`x:Property`) and variables (`Sequence.Variables`). |
| `manage_project_file` | Creates, edits, or deletes a `.md` / `.json` / `.txt` file. Refuses `project.json`, plan files, `docs/knowledge`, `docs/adr`, secret names, and `***REDACTED***` bodies. |
| `patch_project_json` | One structured `project.json` operation (entry points, dependencies, fileInfoCollection, exception handler, runtimeOptions). Never changes `expressionLanguage`, `targetFramework`, or `schemaVersion`. |
| `manage_project_docs` | Lists, writes, deletes, or keyword-searches knowledge articles and ADRs (`kind`: memory / adr / context / all). |
| `sync_project_context` | Regenerates `AGENTS.md` (marker block) and `.claude/rules/project-context.md` from the project model. |
| `validate_project_docs` | Inspects docs without changing plan state. Error findings are the same ones that refuse `update_plan_task(done)` and `verify_work` auto-done. |
| `add_coded_workflow` | Adds a Coded Workflow `.cs` (inherits `CodedWorkflow`, `[Workflow]` entry method, registered in `project.json` `entryPoints`) or a plain coded source file. |
| `create_implementation_plan` | Creates an implementation plan for a project from a goal + ordered task list; writes `docs/implementation-plan.json` (source of truth) plus a Markdown mirror. Refuses to overwrite unless `overwrite: true`. |
| `update_plan_task` | Updates a single plan task's status (`pending`/`in_progress`/`done`/`blocked`) and optional notes. |
| `get_implementation_plan` | Returns the project's implementation plan with derived per-status task counts. |
| `analyze_project_gaps` | Deterministic hygiene gap analysis over the project model (entry point, orphan workflows, exception handling, logging, descriptions, tests, unresolved invokes) plus plan cross-checks; each gap names the MCP tool that fixes it. |
| `verify_work` | Rebuilds the model, runs CLI validation (`uip rpa validate`; optional `build`, default `build: false`), checks expected/planned files exist, and marks the given plan tasks `done` or `blocked` accordingly (statuses untouched when the CLI cannot run; BUILD failure does not auto-block). |
| `find_code_symbol` | Finds C# symbols (methods, classes, properties, fields, interfaces) by exact name using Roslyn semantic analysis; returns kind, file, line, containing type, signature. |
| `find_code_references` | Finds all usage sites of a C# symbol across the project's `.cs` files (semantic matching with an identifier-matching fallback for external symbols). |
| `get_code_context` | Returns the semantic context of one C# member (located by symbol name or file+line): signature, containing type, called methods, referenced types, and the member's source. |
| `get_compile_errors` | Structured Roslyn compiler diagnostics (file/line/column/code/severity/message) without running a build; responses include `analysisMode` (`full`/`partial`/`syntaxOnly`). |
| `compile_project` | Authoritative UiPath CLI build (`uip rpa build`) returning structured compiler errors/warnings. |
| `search_codebase` | Substring search across a project's `.xaml` and `.cs` files in four modes: `text` (matching lines), `symbol` (C# symbols via Roslyn, optional `kind` filter), `activity` (XAML activities by display name/type), `workflow` (workflows by file name/description). Exact-case matches order first; capped at 200 matches with a `truncated` flag. |
| `run_ui_path_cli` | Runs an allowlisted UiPath CLI (`uip`) command (default verbs: `rpa`, `solution`); mutating subcommands are blocked unless enabled in config, shell metacharacters are rejected, and stdout/stderr are redacted and capped. |
| `list_skills` | Lists RPA playbooks only (`uipath-rpa`, `guided-implementation-loop`). Not a full UiPath product catalog. |
| `read_skill` | Reads one RPA skill (`SKILL.md` or an auxiliary file). |

---

## 1. Prerequisites

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

---

## 2. Configure allowed project roots

Edit `src/UiPath.Engineering.Mcp.Server/appsettings.json` and set `Projects:AllowedRoots`
to the folders that contain your UiPath projects. Only paths **inside** these roots can
be analyzed or validated (security guard). Example:

```json
"Projects": {
  "AllowedRoots": [
    "C:/Users/arauj/OneDrive/Documentos/UiPath",
    "C:/Users/arauj/Documents/uipath"
  ]
}
```

`UiPathCli:IncludeRawOutput` is `false` by default (raw CLI console output is suppressed).
`appsettings.Development.json` turns it on for local debugging.

---

## 3. Build & run locally

Quickest path — use the helper script (restores, builds, tests, runs, and checks /health):

```powershell
./scripts/run-local.ps1            # add -SkipTests to skip the test run
```

Or do it manually:

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

The server listens on `http://localhost:5000`.

Verify the endpoints:

```powershell
# Health -> should return 200 "Healthy"
Invoke-WebRequest http://localhost:5000/health

# MCP (Streamable HTTP) endpoint is at /sse
```

The fastest functional test of the tools is the official MCP Inspector:

```powershell
npx @modelcontextprotocol/inspector
# Connect to: http://localhost:5000/sse  (transport: Streamable HTTP)
# You should see analyze_project, validate_project, explain_workflow,
# generate_documentation, search_repository and create_work_items listed.
```

Local agent stdio: see [docs/agent-connection.md](docs/agent-connection.md).

---

## 4. Expose via Microsoft Dev Tunnel

Quickest path — use the helper script (idempotent create + host, prints the public URLs):

```powershell
./scripts/setup-devtunnel.ps1 -Anonymous     # anonymous is fine for local testing only
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

> For initial testing, allow **anonymous** tunnel access. For production, switch to
> Entra ID or Dev Tunnel access control before registering in Copilot.

---

## 5. Register in Microsoft 365 Copilot

- **Name:** UiPath Engineering MCP
- **Endpoint:** `https://<id>-5000.devtunnels.ms/sse`
- **Agent instructions:** paste [docs/copilot-studio-agent-instructions.txt](docs/copilot-studio-agent-instructions.txt) (RPA-only; green gate is `validate_project(build:false, pack:false)` then `update_plan_task`).
- **Recommended tools (default connector):** `analyze_project`, `search_codebase`, `read_workflow_file`, `list_skills`, `read_skill`, `get_implementation_plan`, `update_plan_task`, `analyze_project_gaps`, `validate_project`, `validate_activity_spec`, `build_workflow`, `insert_activities`, `manage_workflow_data`, `manage_project_file`, `patch_project_json`, `manage_project_docs`, `sync_project_context`, `validate_project_docs`
- **Leave off the default connector unless needed:** `search_repository`, `create_work_items`, `run_ui_path_cli`, `verify_work`, `compile_project`, `create_implementation_plan` (create only when no plan exists; prefer get/update)
- **Full tool surface** (Inspector / local agents): `analyze_project`, `validate_project`, `explain_workflow`, `generate_documentation`, `search_repository`, `create_work_items`, `create_project`, `add_xaml_workflow`, `write_workflow_file`, `add_coded_workflow`, `read_workflow_file`, `edit_workflow_file`, `find_activity`, `get_workflow_dependencies`, `edit_workflow_activity`, `validate_activity_spec`, `build_workflow`, `insert_activities`, `manage_workflow_data`, `manage_project_file`, `patch_project_json`, `manage_project_docs`, `sync_project_context`, `validate_project_docs`, `create_implementation_plan`, `update_plan_task`, `get_implementation_plan`, `analyze_project_gaps`, `verify_work`, `find_code_symbol`, `find_code_references`, `get_code_context`, `get_compile_errors`, `compile_project`, `search_codebase`, `run_ui_path_cli`, `list_skills`, `read_skill`

This server is **RPA only** (`.xaml` / `.cs`). Do not install the full `uip skills` marketplace catalog into `.agents/skills`.

---

## 5a. Spec-based workflow authoring (default)

The **default** way to author workflows is now spec-based: describe the workflow
as a JSON activity spec and let the server render schema-correct XAML — no
hand-written fragments. `validate_activity_spec` dry-runs a spec against the
activity catalog and returns every violation in one round trip; `build_workflow`
creates a new workflow from a spec; `insert_activities` adds spec-described
activities into an existing workflow; `manage_workflow_data` adds/removes/renames
arguments and variables. `edit_workflow_activity`'s fragment mode remains as an
escape hatch for surgical edits the spec model does not cover.

Spec shape: `{ name, properties, children, variables (root only), catches (TryCatch only) }`.

Example spec:

```json
{
  "name": "Sequence",
  "variables": [{ "name": "rowCount", "type": "Int32", "default": "0" }],
  "children": [
    {
      "name": "ForEach",
      "properties": { "values": "[in_TransactionData]", "typeArgument": "DataRow" },
      "children": [
        {
          "name": "TryCatch",
          "children": [
            { "name": "LogMessage", "properties": { "message": "\"Processing row\"", "level": "Info" } }
          ],
          "catches": [{ "exception": "System.Exception", "children": [ { "name": "Rethrow" } ] }]
        }
      ]
    }
  ]
}
```

Strings enclosed in square brackets ([expr]) are interpreted as expressions in the project's configured expression language. All other values are treated as literals.

---

## 5b. Autonomous loop (client-driven)

The server answers one deterministic tool call at a time. The client drives the sequence:

```
analyze_project (summary) → analyze_project_gaps
   → get_implementation_plan (create_implementation_plan only if none exists)
   → authoring tools implement each task
   → validate_project(build:false, pack:false) → update_plan_task
```

Example prompt:

> Analyze my UiPath project (summary), resume the existing implementation plan if present,
> implement the next pending task, validate with `validate_project` build false pack false,
> then mark the task done with `update_plan_task`.

Plans live at `docs/implementation-plan.json` (source of truth) plus a Markdown mirror.
Do not use `verify_work` as the done gate. It defaults `build: false` and does not
auto-block on a build-only failure; the green gate is still
`validate_project(build:false, pack:false)` then `update_plan_task`. Connection
recipes and traps: [docs/agent-connection.md](docs/agent-connection.md).

---

## 5c. Run the tests

```powershell
dotnet test
```

The `tests/` folder contains three xUnit projects:

| Project | Covers |
|---------|--------|
| `UiPath.Engineering.Mcp.Core.Tests` | `project.json` parsing, `ProjectModelBuilder` (xaml + coded `.cs` files), `XamlWorkflowParser` (arguments/variables/try-catch/invokes/log messages, malformed xaml), `CodedSourceFileParser` (namespace/class/`[Workflow]`/public methods, malformed input), `DependencyGraphBuilder` (chains, cycles, orphans), XAML/C# file templates (x:Class naming, namespace sanitization), `ImplementationPlanStore` round-trip, `ProjectGapAnalyzer` rule coverage, project file policy, JSON patcher, knowledge/ADR stores, context renderer, docs validator and search. |
| `UiPath.Engineering.Mcp.Providers.Tests` | Path allow-listing (root/child allowed, sibling-prefix & unrelated rejected), filesystem write/delete guards (writes/deletes outside allowed roots throw), `.xaml`/`.cs` discovery skipping `bin`/`obj`/`.git`, `GetDirectoryTree` (depth/ignore/missing dir), `CliExecutableResolver` (explicit path, exe/cmd/ps1 priority, extension fallback), `UiPathCliOutputParser` (analyzer/NuGet/fallback formats), `UiPathCliProvider` per-step results and CLI-not-found error, `GitStatusParser` (porcelain/ahead-behind/not-a-repo), `GitLabProvider` (search/create, token never surfaced). |
| `UiPath.Engineering.Mcp.Tools.Tests` | Tools including the five project-docs tools (`manage_project_file`, `patch_project_json`, `manage_project_docs`, `sync_project_context`, `validate_project_docs`), path-not-allowed, project.json-not-found, happy path, per-step validate output shape, workflow-not-found, parse-error surfacing, GitLab search/create shapes, authoring guards (path-escape, extension allowlist, existing-file), coded-workflow entry-point registration, `uip rpa init` argument shape + partial-success handling, activity-level editing, spec-based authoring, `read_workflow_file`, `edit_workflow_file`, plan create/update/get (including docs-gated `done`), gap-analysis shape (including docs errors), `verify_work` CLI success/failure/unavailable branches with task status transitions and docs-gate refusal, C# analysis tools, `search_codebase`, and structured error propagation (no raw exceptions). |

Tests use hand-written fakes (no Moq) so there are no extra runtime dependencies.

## 6. Project layout

```
src/
  UiPath.Engineering.Mcp.Server/     # ASP.NET host: DI, config, /health, /sse (MapMcp)
  UiPath.Engineering.Mcp.Core/       # Models, config options, project.json + XAML parsing, dependency graph
  UiPath.Engineering.Mcp.Providers/  # Filesystem + UiPath CLI providers (structured CLI output parser)
  UiPath.Engineering.Mcp.Tools/      # [McpServerTool] classes (analyze/validate/explain/document/author)
```

## 7. Notes / known limitations (v4)

- Authoring tools (`create_project`, `add_xaml_workflow`, `write_workflow_file`,
  `add_coded_workflow`, `edit_workflow_activity`, `build_workflow`, `insert_activities`,
  `manage_workflow_data`) **write to disk** — they are restricted to
  `Projects:AllowedRoots`, reject path escapes outside the target project, and only accept
  `.xaml`/`.cs` content.
  `create_project` delegates scaffolding to `uip rpa init` (files are never hand-written);
  `uip rpa init`'s documented partial-success case is detected by checking the created files.
- Spec-based authoring (`validate_activity_spec`, `build_workflow`, `insert_activities`,
  `manage_workflow_data`) is the **default** way to author workflows: specs are validated
  against the activity catalog before anything is written. `edit_workflow_activity`'s
  fragment mode remains as an escape hatch for edits the spec model does not cover.
  In the spec model an `If` activity's `children` are the **Then** branch only — there
  is no Else branch yet. `manage_workflow_data` rename updates the declaration only;
  expressions referencing the old name are not rewritten.
- `edit_workflow_activity` matches the target activity by `DisplayName` (exact, case-sensitive);
  when several activities share a name the edit is rejected and `activityType` must be passed
  to disambiguate. Inserted fragments are re-indented to match the container; the rest of the
  file is preserved byte-for-byte.
- `add_coded_workflow` registers coded workflows in `project.json` `entryPoints` with a
  generated GUID; plain source files are deliberately not registered.
- Publish/deploy to Orchestrator (`uip solution publish/deploy`) is a separate future phase.

- `analyze_project` caches a full project model (fingerprint: SHA-256 of sorted path + last-write ticks for project.json, *.xaml, and *.cs (renames invalidate the cache even when timestamps are preserved)) but the default MCP response is a **summary** (counts,
  workflow index, packages, risks, folder tree) without activity trees. Pass `detail='full'`
  to page complete workflow models (`page`/`pageSize`), or `workflowFile` to load one
  workflow fully. Cycles and orphan workflows are flagged as risks.
- `validate_project` requires the UiPath CLI (`uip`, npm `@uipath/cli`); the executable
  is resolved on PATH among `uip.exe`/`uip.cmd`/`uip.bat`/`uip.ps1` (script shims are
  launched via `cmd.exe`/`powershell.exe`). On non-Windows / missing CLI it returns a
  structured error instead of crashing.
- The `/sse` path serves the **Streamable HTTP** transport (not legacy SSE); the name is
  kept only to match the Copilot registration docs.
- The C# analysis tools (`find_code_symbol`, `find_code_references`, `get_code_context`,
  `get_compile_errors`) build a cached in-memory Roslyn compilation per project. When
  NuGet package assemblies cannot be resolved the response reports
  `analysisMode: "partial"` (some references missing — results may be incomplete) or
  `"syntaxOnly"` (NuGet folder unreachable — declaration/name matching only), so the
  client always knows how much to trust the result.
- The PowerShell provider is a planned phase, not yet implemented.
