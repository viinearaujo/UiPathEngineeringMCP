# UiPath Engineering MCP

A custom **.NET 8** Model Context Protocol (MCP) server that lets an AI client
(Microsoft 365 Copilot, MCP Inspector, Claude, etc.) analyze and validate UiPath
RPA projects over HTTP, exposed to the outside world with **Microsoft Dev Tunnel**.

This is the **MVP / POC (v4)** milestone. Eleven tools are implemented:

| Tool | What it does |
|------|--------------|
| `analyze_project` | Parses `project.json` + deep-parses every `.xaml` workflow (arguments, variables, activities, try/catch, invokes, log messages) and returns structured JSON with risks (cycles, orphan workflows). |
| `validate_project` | Runs `uip.exe` (`restore` / `analyze` / `pack`) and returns structured per-step results (`executed`/`success`/`errors`/`warnings` each) plus recommendations. |
| `explain_workflow` | Returns the structured breakdown of a single workflow: arguments, variables, activity outline, exception handlers, invoked workflows, log messages. |
| `generate_documentation` | Returns deterministic structured documentation data for the whole project: metadata, per-workflow summaries, dependency graph (edges, cycles, orphans), risks. |
| `search_repository` | Searches GitLab issues for the configured project (requires the `GitLab` config section; token is never returned). |
| `create_work_items` | Creates GitLab issues/work items from a list of `{ title, description, labels? }`, returning created IDs/URLs and per-item failures. |
| `create_project` | Scaffolds a new UiPath project via `uip rpa init` (requires the UiPath CLI RPA tool installed on the host). Detects the documented partial-success case by checking the created files. |
| `add_xaml_workflow` | Adds a blank `.xaml` workflow to an existing project, with the correct `x:Class` naming (relative path, separators → underscores). |
| `write_workflow_file` | Creates or fully overwrites a `.xaml` or `.cs` file inside a project with caller-supplied content (extension allowlist + path-escape guard). |
| `edit_workflow_activity` | Activity-level XAML editing: insert an activity fragment into a container, replace, or remove an activity located by `DisplayName` (optional `activityType` disambiguation). Whitespace-preserving; fragments understand unprefixed WF activities plus the `ui:`/`x:` prefixes. |
| `add_coded_workflow` | Adds a Coded Workflow `.cs` (inherits `CodedWorkflow`, `[Workflow]` entry method, registered in `project.json` `entryPoints`) or a plain coded source file. |

---

## 1. Prerequisites

- Windows
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- UiPath CLI (`uip.exe`) on `PATH` (only required for `validate_project`)
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
    "C:/Users/viniciusaraujo/Documents/latam_reconciliation",
    "C:/Users/viniciusaraujo/Documents/uipath"
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
- **Tools:** `analyze_project`, `validate_project`, `explain_workflow`, `generate_documentation`, `search_repository`, `create_work_items`, `create_project`, `add_xaml_workflow`, `write_workflow_file`, `add_coded_workflow`, `edit_workflow_activity`

---

## 5b. Run the tests

```powershell
dotnet test
```

The `tests/` folder contains three xUnit projects:

| Project | Covers |
|---------|--------|
| `UiPath.Engineering.Mcp.Core.Tests` | `project.json` parsing, `ProjectModelBuilder`, `XamlWorkflowParser` (arguments/variables/try-catch/invokes/log messages, malformed xaml), `DependencyGraphBuilder` (chains, cycles, orphans), XAML/C# file templates (x:Class naming, namespace sanitization). |
| `UiPath.Engineering.Mcp.Providers.Tests` | Path allow-listing (root/child allowed, sibling-prefix & unrelated rejected), filesystem write guards (writes outside allowed roots throw), `.xaml` discovery skipping `bin`/`obj`/`.git`, `GetDirectoryTree` (depth/ignore/missing dir), `UiPathCliOutputParser` (analyzer/NuGet/fallback formats), `UiPathCliProvider` per-step results and missing-`uip.exe` error, `GitStatusParser` (porcelain/ahead-behind/not-a-repo), `GitLabProvider` (search/create, token never surfaced). |
| `UiPath.Engineering.Mcp.Tools.Tests` | All eleven tools: path-not-allowed, project.json-not-found, happy path, per-step validate output shape, workflow-not-found, parse-error surfacing, GitLab search/create shapes, authoring guards (path-escape, extension allowlist, existing-file), coded-workflow entry-point registration, `uip rpa init` argument shape + partial-success handling, activity-level editing (insert first/last, replace, remove, ambiguous-target and invalid-fragment errors), and structured error propagation (no raw exceptions). |

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
  `add_coded_workflow`, `edit_workflow_activity`) **write to disk** — they are restricted to
  `Projects:AllowedRoots`, reject path escapes outside the target project, and only accept
  `.xaml`/`.cs` content.
  `create_project` delegates scaffolding to `uip rpa init` (files are never hand-written);
  `uip rpa init`'s documented partial-success case is detected by checking the created files.
- `edit_workflow_activity` matches the target activity by `DisplayName` (exact, case-sensitive);
  when several activities share a name the edit is rejected and `activityType` must be passed
  to disambiguate. Inserted fragments are re-indented to match the container; the rest of the
  file is preserved byte-for-byte.
- `add_coded_workflow` registers coded workflows in `project.json` `entryPoints` with a
  generated GUID; plain source files are deliberately not registered.
- Publish/deploy to Orchestrator (`uip solution publish/deploy`) is a separate future phase.

- `analyze_project` deep-parses `.xaml` workflows (arguments, variables, activity outline,
  try/catch, invokes, log messages), includes the project folder structure, and flags
  cycles/orphan workflows as risks. Project models are cached across requests
  (fingerprint: file count + newest write time).
- `validate_project` requires `uip.exe`; on non-Windows / missing CLI it returns a
  structured error instead of crashing.
- The `/sse` path serves the **Streamable HTTP** transport (not legacy SSE); the name is
  kept only to match the Copilot registration docs.
- The PowerShell provider is a planned phase, not yet implemented.
