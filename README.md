# UiPath Engineering MCP

A custom **.NET 8** Model Context Protocol (MCP) server that lets an AI client
(Microsoft 365 Copilot, MCP Inspector, Claude, etc.) analyze and validate UiPath
RPA projects over HTTP, exposed to the outside world with **Microsoft Dev Tunnel**.

This is the **MVP / POC (v4)** milestone. Twenty-two tools are implemented:

| Tool | What it does |
|------|--------------|
| `analyze_project` | Parses `project.json` + deep-parses every `.xaml` workflow (arguments, variables, activities, try/catch, invokes, log messages) and every `.cs` coded workflow/source file (namespace, class, `[Workflow]` entry methods, public methods) and returns structured JSON with risks (cycles, orphan workflows). |
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
| `edit_workflow_activity` | Activity-level XAML editing: insert an activity fragment into a container, replace, or remove an activity located by `DisplayName` (optional `activityType` disambiguation). Whitespace-preserving; fragments understand unprefixed WF activities plus the `ui:`/`x:` prefixes. |
| `validate_activity_spec` | Dry-run validation of a JSON activity spec against the UiPath activity catalog — no files read or written. Returns every violation as a structured error (`errorCode`/`message`/`fixHint`), or the list of catalog activities the spec uses. |
| `build_workflow` | Creates a real `.xaml` workflow file in a project from a JSON activity spec (run `validate_activity_spec` first). Never overwrites an existing file unless `overwrite: true`. |
| `insert_activities` | Inserts activities described by a JSON activity spec into an existing `.xaml` workflow, as children of the activity located by `DisplayName` — the spec-based sibling of `edit_workflow_activity`. |
| `manage_workflow_data` | Manages the data surface of an existing `.xaml` workflow: add, remove, or rename arguments (`x:Property`) and variables (`Sequence.Variables`). |
| `add_coded_workflow` | Adds a Coded Workflow `.cs` (inherits `CodedWorkflow`, `[Workflow]` entry method, registered in `project.json` `entryPoints`) or a plain coded source file. |
| `create_implementation_plan` | Creates an implementation plan for a project from a goal + ordered task list; writes `docs/implementation-plan.json` (source of truth) plus a Markdown mirror. Refuses to overwrite unless `overwrite: true`. |
| `update_plan_task` | Updates a single plan task's status (`pending`/`in_progress`/`done`/`blocked`) and optional notes. |
| `get_implementation_plan` | Returns the project's implementation plan with derived per-status task counts. |
| `analyze_project_gaps` | Deterministic hygiene gap analysis over the project model (entry point, orphan workflows, exception handling, logging, descriptions, tests, unresolved invokes) plus plan cross-checks; each gap names the MCP tool that fixes it. |
| `verify_work` | Rebuilds the model, runs CLI validation (`uip rpa validate` + `build`), checks expected/planned files exist, and marks the given plan tasks `done` or `blocked` accordingly (statuses untouched when the CLI cannot run). |

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
- **Tools:** `analyze_project`, `validate_project`, `explain_workflow`, `generate_documentation`, `search_repository`, `create_work_items`, `create_project`, `add_xaml_workflow`, `write_workflow_file`, `add_coded_workflow`, `read_workflow_file`, `edit_workflow_file`, `edit_workflow_activity`, `validate_activity_spec`, `build_workflow`, `insert_activities`, `manage_workflow_data`, `create_implementation_plan`, `update_plan_task`, `get_implementation_plan`, `analyze_project_gaps`, `verify_work`

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

## 5b. Autonomous loop (Copilot-driven)

The plan/gap/verify tools let Copilot run a full autonomous development loop. The
server stays passive — it answers one deterministic tool call at a time and never
runs a loop itself; Copilot drives the sequence inside a single request, and only
when you ask for it:

```
analyze_project → analyze_project_gaps → create_implementation_plan
   → (authoring tools implement each task) → verify_work → repeat
```

Example prompt:

> Analyze my UiPath project, create an implementation plan for the remaining work,
> implement it task by task, and verify each step with `verify_work`.

Plans live inside the target project at `docs/implementation-plan.json` (source of
truth) plus a regenerated Markdown mirror. `verify_work` re-validates via the UiPath
CLI and marks plan tasks `done`/`blocked`; when the CLI cannot run it reports the
error and leaves task statuses unchanged. Stopping the loop = ending the Copilot
request.

---

## 5c. Run the tests

```powershell
dotnet test
```

The `tests/` folder contains three xUnit projects:

| Project | Covers |
|---------|--------|
| `UiPath.Engineering.Mcp.Core.Tests` | `project.json` parsing, `ProjectModelBuilder` (xaml + coded `.cs` files), `XamlWorkflowParser` (arguments/variables/try-catch/invokes/log messages, malformed xaml), `CodedSourceFileParser` (namespace/class/`[Workflow]`/public methods, malformed input), `DependencyGraphBuilder` (chains, cycles, orphans), XAML/C# file templates (x:Class naming, namespace sanitization), `ImplementationPlanStore` round-trip, `ProjectGapAnalyzer` rule coverage. |
| `UiPath.Engineering.Mcp.Providers.Tests` | Path allow-listing (root/child allowed, sibling-prefix & unrelated rejected), filesystem write guards (writes outside allowed roots throw), `.xaml`/`.cs` discovery skipping `bin`/`obj`/`.git`, `GetDirectoryTree` (depth/ignore/missing dir), `CliExecutableResolver` (explicit path, exe/cmd/ps1 priority, extension fallback), `UiPathCliOutputParser` (analyzer/NuGet/fallback formats), `UiPathCliProvider` per-step results and CLI-not-found error, `GitStatusParser` (porcelain/ahead-behind/not-a-repo), `GitLabProvider` (search/create, token never surfaced). |
| `UiPath.Engineering.Mcp.Tools.Tests` | All twenty-two tools: path-not-allowed, project.json-not-found, happy path, per-step validate output shape, workflow-not-found, parse-error surfacing, GitLab search/create shapes, authoring guards (path-escape, extension allowlist, existing-file), coded-workflow entry-point registration, `uip rpa init` argument shape + partial-success handling, activity-level editing (insert first/last, replace, remove, ambiguous-target and invalid-fragment errors), spec-based authoring (spec validation error codes, `build_workflow` happy path + overwrite guard, `insert_activities` targeting, `manage_workflow_data` add/remove/rename), `read_workflow_file` (line numbering, `startLine`/`lineCount` pagination, secret redaction, `.env`/`*.pem`/`*.key` refusal), `edit_workflow_file` (exact-match replace, zero/ambiguous-match errors, `replaceAll`), plan create/update/get (overwrite guard, unknown task, no-plan), gap-analysis shape, `verify_work` CLI success/failure/unavailable branches with task status transitions, and structured error propagation (no raw exceptions). |

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

- `analyze_project` deep-parses `.xaml` workflows (arguments, variables, activity outline,
  try/catch, invokes, log messages) and `.cs` coded workflows/source files (namespace,
  class, `[Workflow]` entry methods, public methods), includes the project folder
  structure, and flags cycles/orphan workflows as risks. Project models are cached
  across requests (fingerprint: file count + newest write time, xaml + cs + project.json).
- `validate_project` requires the UiPath CLI (`uip`, npm `@uipath/cli`); the executable
  is resolved on PATH among `uip.exe`/`uip.cmd`/`uip.bat`/`uip.ps1` (script shims are
  launched via `cmd.exe`/`powershell.exe`). On non-Windows / missing CLI it returns a
  structured error instead of crashing.
- The `/sse` path serves the **Streamable HTTP** transport (not legacy SSE); the name is
  kept only to match the Copilot registration docs.
- The PowerShell provider is a planned phase, not yet implemented.
