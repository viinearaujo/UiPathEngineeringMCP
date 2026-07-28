# UiPath Engineering MCP

A custom **.NET 8** Model Context Protocol (MCP) server that lets an AI client
(Microsoft 365 Copilot, MCP Inspector, Claude, etc.) analyze and validate UiPath
RPA projects over HTTP, exposed to the outside world with **Microsoft Dev Tunnel**.

This is the **MVP / POC (v3)** milestone. Six tools are implemented:

| Tool | What it does |
|------|--------------|
| `analyze_project` | Parses `project.json` + deep-parses every `.xaml` workflow (arguments, variables, activities, try/catch, invokes, log messages) and returns structured JSON with risks (cycles, orphan workflows). |
| `validate_project` | Runs `uip.exe` (`restore` / `analyze` / `pack`) and returns structured per-step results (`executed`/`success`/`errors`/`warnings` each) plus recommendations. |
| `explain_workflow` | Returns the structured breakdown of a single workflow: arguments, variables, activity outline, exception handlers, invoked workflows, log messages. |
| `generate_documentation` | Returns deterministic structured documentation data for the whole project: metadata, per-workflow summaries, dependency graph (edges, cycles, orphans), risks. |
| `search_repository` | Searches GitLab issues for the configured project (requires the `GitLab` config section; token is never returned). |
| `create_work_items` | Creates GitLab issues/work items from a list of `{ title, description, labels? }`, returning created IDs/URLs and per-item failures. |

---

## 1. Prerequisites

- Windows
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- UiPath CLI (`uip.exe`) on `PATH` (only required for `validate_project`)
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
- **Tools:** `analyze_project`, `validate_project`, `explain_workflow`, `generate_documentation`, `search_repository`, `create_work_items`

---

## 5b. Run the tests

```powershell
dotnet test
```

The `tests/` folder contains three xUnit projects:

| Project | Covers |
|---------|--------|
| `UiPath.Engineering.Mcp.Core.Tests` | `project.json` parsing, `ProjectModelBuilder`, `XamlWorkflowParser` (arguments/variables/try-catch/invokes/log messages, malformed xaml), `DependencyGraphBuilder` (chains, cycles, orphans). |
| `UiPath.Engineering.Mcp.Providers.Tests` | Path allow-listing (root/child allowed, sibling-prefix & unrelated rejected), `.xaml` discovery skipping `bin`/`obj`/`.git`, `GetDirectoryTree` (depth/ignore/missing dir), `UiPathCliOutputParser` (analyzer/NuGet/fallback formats), `UiPathCliProvider` per-step results and missing-`uip.exe` error, `GitStatusParser` (porcelain/ahead-behind/not-a-repo), `GitLabProvider` (search/create, token never surfaced). |
| `UiPath.Engineering.Mcp.Tools.Tests` | All six tools: path-not-allowed, project.json-not-found, happy path, per-step validate output shape, workflow-not-found, parse-error surfacing, GitLab search/create shapes, and structured error propagation (no raw exceptions). |

Tests use hand-written fakes (no Moq) so there are no extra runtime dependencies.

## 6. Project layout

```
src/
  UiPath.Engineering.Mcp.Server/     # ASP.NET host: DI, config, /health, /sse (MapMcp)
  UiPath.Engineering.Mcp.Core/       # Models, config options, project.json + XAML parsing, dependency graph
  UiPath.Engineering.Mcp.Providers/  # Filesystem + UiPath CLI providers (structured CLI output parser)
  UiPath.Engineering.Mcp.Tools/      # [McpServerTool] classes (analyze/validate/explain/document)
```

## 7. Notes / known limitations (v2)

- `analyze_project` deep-parses `.xaml` workflows (arguments, variables, activity outline,
  try/catch, invokes, log messages) and flags cycles/orphan workflows as risks.
  Folder-structure output and cross-request model caching are later phases.
- `validate_project` requires `uip.exe`; on non-Windows / missing CLI it returns a
  structured error instead of crashing.
- The `/sse` path serves the **Streamable HTTP** transport (not legacy SSE); the name is
  kept only to match the Copilot registration docs.
- Git/GitLab repository intelligence tools (`search_repository`, `create_work_items`)
  and the PowerShell provider are planned phases, not yet implemented.
