# AGENTS.md

## Cursor Cloud specific instructions

This repo is a **.NET 8** ASP.NET Core solution (`UiPath.Engineering.Mcp.sln`) implementing
the "UiPath Engineering MCP" server. The README lists Windows as a prerequisite, but the
server and libraries are plain cross-platform ASP.NET Core / Roslyn — they build, test, and
run on Linux. The `scripts/*.ps1` helpers are Windows/PowerShell only; on this VM invoke
`dotnet` directly instead.

### Toolchain
- .NET 8 SDK is installed system-wide (`dotnet` on `PATH`) and refreshed by the startup
  update script (`dotnet restore`). Node 22 is present but not required to build/run.

### Build / test / lint / run
- Build: `dotnet build --configuration Debug` (from repo root; restores are cached).
- Test: `dotnet test` (xUnit; no external services or network needed).
- Lint/format: `dotnet format --verify-no-changes` (uses `.editorconfig`).
- Run the server: `cd src/UiPath.Engineering.Mcp.Server && dotnet run`. It listens on
  `http://localhost:5000` with `GET /health` (returns `Healthy`) and the MCP Streamable-HTTP
  transport at `POST /sse` (the `/sse` name is kept for Copilot docs; it is not legacy SSE).

### Non-obvious caveats (durable)
- **7 tests fail on Linux by design.** `CliExecutableResolverTests` in
  `UiPath.Engineering.Mcp.Providers.Tests` hardcode Windows paths (`C:\npm`, `\` separators)
  and assert `cmd.exe`/`powershell.exe` shim resolution. They pass only on Windows; the other
  ~518 tests pass. This is not a regression on this VM.
- **`dotnet format` reports `ENDOFLINE` (CRLF) violations.** `.editorconfig` mandates `crlf`
  but the files are committed with `LF` and there is no `.gitattributes` to normalize on
  checkout. This is a pre-existing, OS-dependent discrepancy — do not mass-rewrite line
  endings to "fix" it unless that is the actual task.
- **Analyzing a real UiPath project requires an allowed root.** `Projects:AllowedRoots` in
  `appsettings.json` points at Windows folders. Path-guarded tools (`analyze_project`,
  authoring, etc.) only accept paths inside an allowed root. To point at a project on this VM,
  override without editing tracked config, e.g. run the server with
  `Projects__AllowedRoots__0=/path/to/projects` (or use a gitignored `appsettings.Local.json`).
- **CLI-backed tools need the UiPath `uip` CLI**, which is not installed (npm `@uipath/cli`,
  Windows-oriented). `validate_project`, `compile_project`, `create_project`, and
  `run_ui_path_cli` return a structured "CLI not found" error rather than crashing; the
  parsing/authoring/analysis tools work without it.
