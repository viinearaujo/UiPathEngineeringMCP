# UiPath Engineering MCP — Implementation Handoff

## 1. Purpose

This document is the execution handoff plan for building a custom Model Context Protocol server for UiPath engineering tasks.

The original reference document remains the Source of Truth:

```text
UiPath_Engineering_MCP_MVP_Implementation_Plan.md
```

This handoff document translates that plan into concrete implementation tasks, environment decisions, architecture, acceptance criteria, and agent prompts.

---

## 2. Confirmed Environment

| Item | Value |
|---|---|
| Target OS | Windows |
| Runtime | .NET 8 |
| Language | C# only |
| MCP transport | HTTP/SSE |
| External exposure | Microsoft Dev Tunnel |
| AI client | Microsoft 365 Copilot |
| UiPath CLI executable | `uip.exe` |
| UiPath CLI availability | Already on PATH |
| Solution location | `C:\Users\arauj\Documents\UiPathEngineeringMCP` |
| Primary test project | `C:\Users\arauj\OneDrive\Documentos\UiPath\testProcess` |
| Additional UiPath projects root | `C:\Users\arauj\Documents\uipath` |

---

## 3. MVP Goal

Build a custom .NET 8 MCP server that Microsoft 365 Copilot can call through Microsoft Dev Tunnel.

The first MVP milestone must support:

```text
analyze_project()
validate_project()
```

The server must return structured JSON, not raw console output.

Target flow:

```text
Microsoft 365 Copilot
  -> Microsoft Dev Tunnel HTTPS endpoint
    -> .NET 8 MCP Server
      -> Filesystem Provider
      -> PowerShell Provider
      -> UiPath CLI Provider
        -> Structured JSON result
```

---

## 4. Non-Negotiable Engineering Rules

- Use C# exclusively.
- Use .NET 8.
- Do not build a chatbot inside the MCP server.
- The MCP server returns deterministic structured data.
- All UiPath parsing must flow through a single Internal Project Model.
- Do not repeatedly parse the same UiPath project files.
- Suppress raw UiPath CLI console output from tool responses by default.
- Tools must return structured JSON.
- External process execution must have timeouts.
- Project paths must be validated against allowed roots.
- Do not allow arbitrary shell command execution from MCP clients.
- Secrets and tokens must never be returned in tool responses.
- Prefer provider isolation over logic inside tools.
- Keep explanations direct, simple, and technical.

---

## 5. First Milestone Scope

The first milestone is complete when:

- The .NET 8 MCP server builds and runs locally.
- The server listens on:

```text
http://localhost:5000
```

- Health endpoint works:

```text
GET /health
```

- MCP HTTP/SSE endpoint works:

```text
GET /sse
```

- Microsoft Dev Tunnel exposes the local server over HTTPS.
- The tunnel health endpoint works.
- The tunnel SSE endpoint is reachable.
- `analyze_project()` returns structured JSON for a real UiPath project.
- `validate_project()` returns structured JSON using `uip.exe`.
- The test project can be analyzed:

```text
C:\Users\arauj\OneDrive\Documentos\UiPath\testProcess
```

---

## 6. Repository Layout

Create the solution here:

```text
C:\Users\arauj\Documents\UiPathEngineeringMCP
```

Target structure:

```text
UiPathEngineeringMCP/
│
├── UiPath.Engineering.Mcp.sln
│
├── src/
│   │
│   ├── UiPath.Engineering.Mcp.Server/
│   │   ├── Program.cs
│   │   ├── appsettings.json
│   │   ├── appsettings.Development.json
│   │   ├── Mcp/
│   │   │   ├── McpServerSetup.cs
│   │   │   └── McpToolRegistry.cs
│   │   └── Endpoints/
│   │       └── HealthEndpoints.cs
│   │
│   ├── UiPath.Engineering.Mcp.Core/
│   │   ├── Models/
│   │   │   ├── UiPathProjectModel.cs
│   │   │   ├── WorkflowModel.cs
│   │   │   ├── PackageModel.cs
│   │   │   ├── AssetModel.cs
│   │   │   ├── QueueModel.cs
│   │   │   ├── VariableModel.cs
│   │   │   ├── ArgumentModel.cs
│   │   │   ├── DependencyModel.cs
│   │   │   ├── ExceptionHandlerModel.cs
│   │   │   ├── InvokeWorkflowModel.cs
│   │   │   └── ToolResult.cs
│   │   │
│   │   ├── Parsing/
│   │   │   ├── IProjectModelBuilder.cs
│   │   │   ├── ProjectModelBuilder.cs
│   │   │   ├── ProjectJsonParser.cs
│   │   │   ├── XamlWorkflowParser.cs
│   │   │   └── DependencyGraphBuilder.cs
│   │   │
│   │   └── Configuration/
│   │       ├── McpServerOptions.cs
│   │       ├── ProjectRootOptions.cs
│   │       ├── UiPathCliOptions.cs
│   │       └── PowerShellOptions.cs
│   │
│   ├── UiPath.Engineering.Mcp.Providers/
│   │   ├── Filesystem/
│   │   │   ├── IFilesystemProvider.cs
│   │   │   └── FilesystemProvider.cs
│   │   │
│   │   ├── PowerShell/
│   │   │   ├── IPowerShellProvider.cs
│   │   │   ├── PowerShellProvider.cs
│   │   │   └── PowerShellResult.cs
│   │   │
│   │   ├── UiPathCli/
│   │   │   ├── IUiPathCliProvider.cs
│   │   │   ├── UiPathCliProvider.cs
│   │   │   ├── UiPathCliResult.cs
│   │   │   └── UiPathCliCommandBuilder.cs
│   │   │
│   │   ├── Git/
│   │   │   ├── IGitProvider.cs
│   │   │   └── GitProvider.cs
│   │   │
│   │   └── GitLab/
│   │       ├── IGitLabProvider.cs
│   │       └── GitLabProvider.cs
│   │
│   └── UiPath.Engineering.Mcp.Tools/
│       ├── AnalyzeProjectTool.cs
│       ├── ValidateProjectTool.cs
│       ├── ExplainWorkflowTool.cs
│       ├── GenerateDocumentationTool.cs
│       ├── SearchRepositoryTool.cs
│       └── CreateWorkItemsTool.cs
│
└── tests/
    ├── UiPath.Engineering.Mcp.Core.Tests/
    ├── UiPath.Engineering.Mcp.Providers.Tests/
    └── UiPath.Engineering.Mcp.Tools.Tests/
```

For the first milestone, only these are required:

```text
UiPath.Engineering.Mcp.Server
UiPath.Engineering.Mcp.Core
UiPath.Engineering.Mcp.Providers
UiPath.Engineering.Mcp.Tools
```

Only these tools are required:

```text
AnalyzeProjectTool
ValidateProjectTool
```

Everything else can remain stubbed.

---

## 7. Scaffold Commands

Run from PowerShell:

```powershell
cd C:\Users\arauj\Documents
mkdir UiPathEngineeringMCP
cd UiPathEngineeringMCP

dotnet new sln -n UiPath.Engineering.Mcp

mkdir src
mkdir tests

cd src
dotnet new web -n UiPath.Engineering.Mcp.Server
dotnet new classlib -n UiPath.Engineering.Mcp.Core
dotnet new classlib -n UiPath.Engineering.Mcp.Providers
dotnet new classlib -n UiPath.Engineering.Mcp.Tools

cd ..\tests
dotnet new xunit -n UiPath.Engineering.Mcp.Core.Tests
dotnet new xunit -n UiPath.Engineering.Mcp.Providers.Tests
dotnet new xunit -n UiPath.Engineering.Mcp.Tools.Tests

cd ..

dotnet sln add src\UiPath.Engineering.Mcp.Server\UiPath.Engineering.Mcp.Server.csproj
dotnet sln add src\UiPath.Engineering.Mcp.Core\UiPath.Engineering.Mcp.Core.csproj
dotnet sln add src\UiPath.Engineering.Mcp.Providers\UiPath.Engineering.Mcp.Providers.csproj
dotnet sln add src\UiPath.Engineering.Mcp.Tools\UiPath.Engineering.Mcp.Tools.csproj

dotnet sln add tests\UiPath.Engineering.Mcp.Core.Tests\UiPath.Engineering.Mcp.Core.Tests.csproj
dotnet sln add tests\UiPath.Engineering.Mcp.Providers.Tests\UiPath.Engineering.Mcp.Providers.Tests.csproj
dotnet sln add tests\UiPath.Engineering.Mcp.Tools.Tests\UiPath.Engineering.Mcp.Tools.Tests.csproj
```

Add project references:

```powershell
dotnet add src\UiPath.Engineering.Mcp.Server reference src\UiPath.Engineering.Mcp.Core
dotnet add src\UiPath.Engineering.Mcp.Server reference src\UiPath.Engineering.Mcp.Providers
dotnet add src\UiPath.Engineering.Mcp.Server reference src\UiPath.Engineering.Mcp.Tools

dotnet add src\UiPath.Engineering.Mcp.Providers reference src\UiPath.Engineering.Mcp.Core
dotnet add src\UiPath.Engineering.Mcp.Tools reference src\UiPath.Engineering.Mcp.Core
dotnet add src\UiPath.Engineering.Mcp.Tools reference src\UiPath.Engineering.Mcp.Providers

dotnet add tests\UiPath.Engineering.Mcp.Core.Tests reference src\UiPath.Engineering.Mcp.Core
dotnet add tests\UiPath.Engineering.Mcp.Providers.Tests reference src\UiPath.Engineering.Mcp.Providers
dotnet add tests\UiPath.Engineering.Mcp.Providers.Tests reference src\UiPath.Engineering.Mcp.Core
dotnet add tests\UiPath.Engineering.Mcp.Tools.Tests reference src\UiPath.Engineering.Mcp.Tools
dotnet add tests\UiPath.Engineering.Mcp.Tools.Tests reference src\UiPath.Engineering.Mcp.Providers
dotnet add tests\UiPath.Engineering.Mcp.Tools.Tests reference src\UiPath.Engineering.Mcp.Core
```

Add MCP SDK packages:

```powershell
dotnet add src\UiPath.Engineering.Mcp.Server package ModelContextProtocol --prerelease
dotnet add src\UiPath.Engineering.Mcp.Server package ModelContextProtocol.AspNetCore --prerelease
```

If package names differ in the current SDK version, adjust during scaffold.

---

## 8. Server Configuration

File:

```text
src\UiPath.Engineering.Mcp.Server\appsettings.json
```

Recommended content:

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  },
  "AllowedHosts": "*",
  "Urls": "http://localhost:5000",
  "McpServer": {
    "Name": "UiPath Engineering MCP",
    "Version": "0.1.0",
    "Description": "MCP server for UiPath engineering analysis, validation, documentation, and repository intelligence."
  },
  "Projects": {
    "AllowedRoots": [
      "C:/Users/arauj/OneDrive/Documentos/UiPath",
      "C:/Users/arauj/Documents/uipath"
    ]
  },
  "UiPathCli": {
    "ExecutablePath": "uip.exe",
    "DefaultTimeoutSeconds": 300,
    "IncludeRawOutput": false,
    "DefaultPackOutputDirectory": "C:/Users/arauj/Documents/UiPathEngineeringMCP/artifacts"
  },
  "PowerShell": {
    "ExecutablePath": "pwsh.exe",
    "FallbackExecutablePath": "powershell.exe",
    "DefaultTimeoutSeconds": 120
  }
}
```

Development override:

```text
src\UiPath.Engineering.Mcp.Server\appsettings.Development.json
```

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Debug",
      "Microsoft.AspNetCore": "Information"
    }
  },
  "UiPathCli": {
    "IncludeRawOutput": true
  }
}
```

---

## 9. Initial Server Skeleton

File:

```text
src\UiPath.Engineering.Mcp.Server\Program.cs
```

Target skeleton:

```csharp
using ModelContextProtocol.AspNetCore;
using UiPath.Engineering.Mcp.Core.Configuration;
using UiPath.Engineering.Mcp.Providers.Filesystem;
using UiPath.Engineering.Mcp.Providers.PowerShell;
using UiPath.Engineering.Mcp.Providers.UiPathCli;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<McpServerOptions>(
    builder.Configuration.GetSection("McpServer"));

builder.Services.Configure<ProjectRootOptions>(
    builder.Configuration.GetSection("Projects"));

builder.Services.Configure<UiPathCliOptions>(
    builder.Configuration.GetSection("UiPathCli"));

builder.Services.Configure<PowerShellOptions>(
    builder.Configuration.GetSection("PowerShell"));

builder.Services.AddSingleton<IFilesystemProvider, FilesystemProvider>();
builder.Services.AddSingleton<IPowerShellProvider, PowerShellProvider>();
builder.Services.AddSingleton<IUiPathCliProvider, UiPathCliProvider>();

builder.Services.AddHealthChecks();

builder.Services.AddMcpServer()
    .WithHttpTransport()
    .WithToolsFromAssembly();

var app = builder.Build();

app.MapHealthChecks("/health");

app.MapMcp("/sse");

app.Run();
```

If the installed MCP SDK version uses slightly different transport mapping, adjust this file first.

The required behavior is:

```text
GET /health
GET /sse
POST /messages
```

---

## 10. Core Configuration Models

File:

```text
src\UiPath.Engineering.Mcp.Core\Configuration\McpServerOptions.cs
```

```csharp
namespace UiPath.Engineering.Mcp.Core.Configuration;

public sealed class McpServerOptions
{
    public string Name { get; init; } = "UiPath Engineering MCP";
    public string Version { get; init; } = "0.1.0";
    public string Description { get; init; } = string.Empty;
}
```

File:

```text
src\UiPath.Engineering.Mcp.Core\Configuration\ProjectRootOptions.cs
```

```csharp
namespace UiPath.Engineering.Mcp.Core.Configuration;

public sealed class ProjectRootOptions
{
    public List<string> AllowedRoots { get; init; } = [];
}
```

File:

```text
src\UiPath.Engineering.Mcp.Core\Configuration\UiPathCliOptions.cs
```

```csharp
namespace UiPath.Engineering.Mcp.Core.Configuration;

public sealed class UiPathCliOptions
{
    public string ExecutablePath { get; init; } = "uip.exe";
    public int DefaultTimeoutSeconds { get; init; } = 300;
    public bool IncludeRawOutput { get; init; }
    public string DefaultPackOutputDirectory { get; init; } = string.Empty;
}
```

File:

```text
src\UiPath.Engineering.Mcp.Core\Configuration\PowerShellOptions.cs
```

```csharp
namespace UiPath.Engineering.Mcp.Core.Configuration;

public sealed class PowerShellOptions
{
    public string ExecutablePath { get; init; } = "pwsh.exe";
    public string FallbackExecutablePath { get; init; } = "powershell.exe";
    public int DefaultTimeoutSeconds { get; init; } = 120;
}
```

---

## 11. Internal Project Model

The Internal Project Model is the central parsing artifact.

All tools should consume this model instead of reading UiPath files directly.

Initial model:

```csharp
namespace UiPath.Engineering.Mcp.Core.Models;

public sealed class UiPathProjectModel
{
    public string ProjectPath { get; init; } = string.Empty;
    public string ProjectName { get; init; } = string.Empty;
    public string? Description { get; init; }
    public string? MainWorkflow { get; init; }
    public string? ProjectJsonPath { get; init; }
    public string? ReadmeSummary { get; init; }

    public List<WorkflowModel> Workflows { get; init; } = [];
    public List<PackageModel> Packages { get; init; } = [];
    public List<AssetModel> Assets { get; init; } = [];
    public List<QueueModel> Queues { get; init; } = [];
    public List<VariableModel> Variables { get; init; } = [];
    public List<ArgumentModel> Arguments { get; init; } = [];
    public List<DependencyModel> Dependencies { get; init; } = [];
    public List<InvokeWorkflowModel> InvokeWorkflows { get; init; } = [];
    public List<ExceptionHandlerModel> ExceptionHandlers { get; init; } = [];
    public List<string> TechnicalDebt { get; init; } = [];
    public List<string> Risks { get; init; } = [];
    public List<string> Recommendations { get; init; } = [];
}
```

---

## 12. Provider Contracts

### Filesystem Provider

Purpose:

- Read files.
- Find `project.json`.
- Find `.xaml` files.
- Build folder structure.
- Ignore irrelevant directories.

Interface:

```csharp
public interface IFilesystemProvider
{
    bool ProjectExists(string projectPath);
    string? FindProjectJson(string projectPath);
    IReadOnlyList<string> FindFiles(string root, string searchPattern, bool recursive = true);
    string ReadAllText(string filePath);
    DirectoryTreeNode GetDirectoryTree(string root, int maxDepth = 3);
}
```

Ignore:

```text
.git
.local
.settings
bin
obj
node_modules
```

---

### PowerShell Provider

Purpose:

- Execute commands safely.
- Capture stdout.
- Capture stderr.
- Capture exit code.
- Enforce timeout.

Interface:

```csharp
public interface IPowerShellProvider
{
    Task<PowerShellResult> ExecuteAsync(
        string command,
        string? workingDirectory = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default);
}
```

Result:

```csharp
public sealed class PowerShellResult
{
    public int ExitCode { get; init; }
    public bool Success => ExitCode == 0;
    public string StandardOutput { get; init; } = string.Empty;
    public string ErrorOutput { get; init; } = string.Empty;
    public TimeSpan Duration { get; init; }
}
```

---

### UiPath CLI Provider

Purpose:

- Wrap `uip.exe`.
- Normalize project path input.
- Run restore, analyze, pack.
- Convert output into structured errors and warnings.
- Avoid returning raw console output by default.

Interface:

```csharp
public interface IUiPathCliProvider
{
    Task<UiPathCliResult> RestoreAsync(string projectPath, CancellationToken cancellationToken = default);
    Task<UiPathCliResult> AnalyzeAsync(string projectPath, CancellationToken cancellationToken = default);
    Task<UiPathCliResult> PackAsync(string projectPath, string outputPath, CancellationToken cancellationToken = default);
    Task<UiPathCliResult> ValidateAsync(string projectPath, bool restore, bool analyze, bool pack, CancellationToken cancellationToken = default);
}
```

Result:

```csharp
public sealed class UiPathCliResult
{
    public bool Success { get; init; }
    public string Command { get; init; } = string.Empty;
    public int ExitCode { get; init; }
    public string Summary { get; init; } = string.Empty;
    public List<string> Errors { get; init; } = [];
    public List<string> Warnings { get; init; } = [];
    public List<string> RawOutputLines { get; init; } = [];
    public TimeSpan Duration { get; init; }
}
```

---

## 13. UiPath CLI Command Mapping

The provider must accept either:

```text
C:\Users\arauj\OneDrive\Documentos\UiPath\testProcess
```

or:

```text
C:\Users\arauj\OneDrive\Documentos\UiPath\testProcess\project.json
```

### Restore

If input is a directory:

```text
Working directory:
  C:\Users\arauj\OneDrive\Documentos\UiPath\testProcess

Command:
  uip.exe restore
```

If input is `project.json`:

```text
Command:
  uip.exe restore "C:\Users\arauj\OneDrive\Documentos\UiPath\testProcess\project.json"
```

### Analyze

If input is a directory:

```text
Working directory:
  C:\Users\arauj\OneDrive\Documentos\UiPath\testProcess

Command:
  uip.exe analyze
```

If input is `project.json`:

```text
Command:
  uip.exe analyze "C:\Users\arauj\OneDrive\Documentos\UiPath\testProcess\project.json"
```

### Pack

Pack expects a project directory:

```text
uip.exe pack "C:\Users\arauj\OneDrive\Documentos\UiPath\testProcess" --output "C:\Users\arauj\Documents\UiPathEngineeringMCP\artifacts"
```

Optional version:

```text
uip.exe pack "C:\Users\arauj\OneDrive\Documentos\UiPath\testProcess" --version 1.0.0 --output "C:\Users\arauj\Documents\UiPathEngineeringMCP\artifacts"
```

---

## 14. First MCP Tools

### Tool: `analyze_project`

Input:

```json
{
  "projectPath": "C:\\Users\\arauj\\OneDrive\\Documentos\\UiPath\\testProcess"
}
```

Expected output shape:

```json
{
  "summary": "Project analyzed successfully.",
  "project": {
    "name": "testProcess",
    "description": "",
    "mainWorkflow": "Main.xaml",
    "projectJsonPath": "C:/Users/arauj/OneDrive/Documentos/UiPath/testProcess/project.json",
    "readmeSummary": ""
  },
  "folderStructure": [],
  "workflows": [],
  "packages": [],
  "dependencies": [],
  "assets": [],
  "queues": [],
  "technicalDebt": [],
  "risks": [],
  "recommendations": []
}
```

Implementation source:

- Filesystem Provider.
- `project.json` parser.
- README parser.
- Basic `.xaml` discovery.
- Package dependency extraction.

---

### Tool: `validate_project`

Input:

```json
{
  "projectPath": "C:\\Users\\arauj\\OneDrive\\Documentos\\UiPath\\testProcess",
  "restore": true,
  "analyze": true,
  "pack": false
}
```

Expected output shape:

```json
{
  "summary": "Validation completed.",
  "success": true,
  "restore": {
    "success": true,
    "errors": [],
    "warnings": []
  },
  "analyze": {
    "success": true,
    "errors": [],
    "warnings": []
  },
  "pack": {
    "success": false,
    "errors": [],
    "warnings": []
  },
  "errors": [],
  "warnings": [],
  "recommendations": []
}
```

Implementation source:

- UiPath CLI Provider.
- PowerShell Provider if needed.
- Structured CLI output parser.

---

## 15. JSON Response Standard

All tools should return structured JSON.

Recommended envelope:

```json
{
  "status": "success",
  "summary": "Operation completed.",
  "data": {},
  "errors": [],
  "warnings": [],
  "durationMs": 0
}
```

For tool-specific responses, it is acceptable to return a strongly shaped object directly, as long as it remains structured JSON.

Example:

```json
{
  "summary": "Project analyzed successfully.",
  "project": {},
  "workflows": [],
  "packages": [],
  "assets": [],
  "queues": [],
  "technicalDebt": [],
  "risks": [],
  "recommendations": []
}
```

---

## 16. Error Handling Standard

Tools must not throw raw exceptions to the MCP client.

Return structured errors.

Example:

```json
{
  "status": "error",
  "summary": "Project validation failed.",
  "errors": [
    "UiPath CLI was not found. Install it or configure the correct executable path."
  ],
  "warnings": [],
  "recommendations": [
    "Verify that uip.exe is available on PATH.",
    "Set UiPathCliOptions.ExecutablePath in appsettings.json."
  ]
}
```

Error categories:

```text
Invalid input
Project path not found
project.json not found
Invalid UiPath project
UiPath CLI not installed
UiPath CLI timeout
PowerShell execution failure
XAML parse failure
Git failure
GitLab authentication failure
Path not allowed
OneDrive file unavailable
```

---

## 17. Security Rules

- Do not allow arbitrary shell commands from MCP clients.
- Only expose typed MCP tools.
- Validate all project paths against allowed roots.
- Prevent path traversal.
- Timeout all external processes.
- Keep raw CLI output disabled by default.
- Do not return secrets.
- Do not return GitLab tokens.
- Log diagnostics server-side only.

Example path validation rule:

```csharp
public bool IsPathAllowed(string requestedPath)
{
    var fullPath = Path.GetFullPath(requestedPath);

    return _options.AllowedRoots
        .Select(Path.GetFullPath)
        .Any(root => fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase));
}
```

---

## 18. OneDrive-Specific Considerations

The first test project is under OneDrive:

```text
C:\Users\arauj\OneDrive\Documentos\UiPath\testProcess
```

Important:

- Ensure the project files are available locally, not online-only.
- Avoid writing build artifacts inside the OneDrive project folder.
- Use a central artifacts folder:

```text
C:\Users\arauj\Documents\UiPathEngineeringMCP\artifacts
```

- Handle file lock errors gracefully.
- Treat OneDrive sync issues as filesystem/provider errors, not MCP server crashes.

---

## 19. Dev Tunnel Setup

Install Dev Tunnel CLI:

```powershell
winget install Microsoft.devtunnel
```

If that fails, use the VS Code Dev Tunnels extension or Microsoft documentation.

Login:

```powershell
devtunnel user login
```

Create tunnel:

```powershell
devtunnel create uipath-mcp
```

Create port:

```powershell
devtunnel port create uipath-mcp --port 5000 --protocol http
```

Host tunnel:

```powershell
devtunnel host uipath-mcp
```

Expected tunnel URL shape:

```text
https://uipath-mcp-5000.devtunnels.ms
```

Actual tunnel ID may differ.

For initial testing, allow anonymous access if prompted.

For production, use Entra ID or tunnel access control.

---

## 20. Local Test Plan

Start server:

```powershell
cd C:\Users\arauj\Documents\UiPathEngineeringMCP\src\UiPath.Engineering.Mcp.Server
dotnet run
```

Test health:

```powershell
Invoke-WebRequest http://localhost:5000/health
```

Expected:

```text
StatusCode: 200
Content: Healthy
```

Test SSE:

```powershell
curl -N http://localhost:5000/sse
```

Expected:

- Connection stays open.
- SSE events stream.
- No HTML error page.

---

## 21. Tunnel Test Plan

Terminal 1:

```powershell
devtunnel host uipath-mcp
```

Terminal 2:

```powershell
cd C:\Users\arauj\Documents\UiPathEngineeringMCP\src\UiPath.Engineering.Mcp.Server
dotnet run
```

Test tunnel health:

```powershell
Invoke-WebRequest https://<tunnel-id>-5000.devtunnels.ms/health
```

Test tunnel SSE:

```powershell
curl -N https://<tunnel-id>-5000.devtunnels.ms/sse
```

---

## 22. Microsoft 365 Copilot Registration Plan

Once the tunnel endpoint is reachable, register the MCP server in Microsoft 365 Copilot.

Registration details:

```text
Name:
  UiPath Engineering MCP

Description:
  Analyzes and validates UiPath projects, explains workflows, generates documentation data, and searches UiPath repositories.

Endpoint:
  https://<tunnel-id>-5000.devtunnels.ms/sse

Tools:
  analyze_project
  validate_project
```

Tool descriptions:

```text
analyze_project:
  Analyzes a UiPath project and returns structured metadata, folder structure, workflows, packages, dependencies, assets, queues, risks, and recommendations.

validate_project:
  Validates a UiPath project using the UiPath CLI and returns structured restore, analyze, pack, error, warning, and recommendation data.
```

If Copilot Studio requires a manifest, generate it after the tunnel URL is known.

If Copilot requires authenticated access, configure Dev Tunnel access control before final registration.

---

## 23. Build Phases

### Phase 0: Scaffold

Tasks:

- Create solution.
- Create projects.
- Add references.
- Add MCP packages.
- Add configuration.
- Add health endpoint.
- Add MCP server skeleton.

Done when:

```text
dotnet build succeeds
dotnet run starts server
GET /health returns Healthy
```

---

### Phase 1: Core Models

Tasks:

- Create configuration models.
- create `UiPathProjectModel`.
- Create workflow, package, dependency, asset, queue models.
- Create `ToolResult`.

Done when:

```text
Models serialize cleanly to JSON
```

---

### Phase 2: Filesystem Provider

Tasks:

- Implement path validation.
- Find `project.json`.
- Find `.xaml` files.
- Build directory tree.
- Ignore noise folders.

Done when:

```text
Provider can inspect testProcess project
```

---

### Phase 3: PowerShell Provider

Tasks:

- Execute commands.
- Capture stdout/stderr.
- Handle timeout.
- Handle missing executable.

Done when:

```text
Provider can run simple PowerShell command
```

---

### Phase 4: UiPath CLI Provider

Tasks:

- Normalize project path.
- Run restore.
- Run analyze.
- Run pack.
- Convert output into structured errors/warnings.
- Suppress raw output by default.

Done when:

```text
Provider can validate testProcess using uip.exe
```

---

### Phase 5: First MCP Tools

Tasks:

- Implement `analyze_project`.
- Implement `validate_project`.
- Register tools with MCP server.
- Return structured JSON.

Done when:

```text
Copilot or MCP client can call both tools successfully
```

---

### Phase 6: XAML Parsing

Tasks:

- Parse `.xaml`.
- Extract arguments.
- Extract variables.
- Extract activities.
- Extract Try Catch blocks.
- Extract Invoke Workflow activities.
- Extract Log Message activities.

Done when:

```text
Workflow structure can be returned as JSON
```

---

### Phase 7: Dependency Graph

Tasks:

- Build workflow invocation graph.
- Detect parent/child relationships.
- Detect cycles.
- Identify orphan workflows.

Done when:

```text
Main.xaml dependency tree can be generated
```

---

### Phase 8: Explain and Documentation Tools

Tasks:

- Implement `explain_workflow`.
- Implement `generate_documentation`.
- Return structured documentation data.

Done when:

```text
Copilot can generate human-readable documentation from tool output
```

---

### Phase 9: Git and GitLab

Tasks:

- Implement Git status provider.
- Implement GitLab issue/story provider.
- Add repository search support.
- Add work item creation support.

Done when:

```text
Repository intelligence tools can use Git and GitLab data
```

---

## 24. MVP Tool Backlog

### MVP-1: Scaffold Server

Input:

```text
Solution path
Project structure
MCP packages
```

Output:

```text
Buildable .NET 8 solution
```

Acceptance:

```text
dotnet build succeeds
GET /health works
```

---

### MVP-2: Core Models

Input:

```text
UiPath project concepts
```

Output:

```text
Internal Project Model
```

Acceptance:

```text
Models serialize to JSON
```

---

### MVP-3: Filesystem Provider

Input:

```text
C:\Users\arauj\OneDrive\Documentos\UiPath\testProcess
```

Output:

```text
project.json location
.xaml files
folder tree
README summary
```

Acceptance:

```text
Can analyze testProcess folder
```

---

### MVP-4: PowerShell Provider

Input:

```text
Command
Working directory
Timeout
```

Output:

```text
Exit code
stdout
stderr
duration
```

Acceptance:

```text
Command execution is reliable and bounded
```

---

### MVP-5: UiPath CLI Provider

Input:

```text
Project path or project.json path
```

Output:

```text
Restore result
Analyze result
Pack result
Structured errors
Structured warnings
```

Acceptance:

```text
Can validate testProcess with uip.exe
```

---

### MVP-6: `analyze_project`

Input:

```json
{
  "projectPath": "C:\\Users\\arauj\\OneDrive\\Documentos\\UiPath\\testProcess"
}
```

Output:

```json
{
  "summary": "Project analyzed successfully.",
  "project": {},
  "workflows": [],
  "packages": [],
  "dependencies": [],
  "assets": [],
  "queues": [],
  "technicalDebt": [],
  "risks": [],
  "recommendations": []
}
```

Acceptance:

```text
Returns valid structured JSON
```

---

### MVP-7: `validate_project`

Input:

```json
{
  "projectPath": "C:\\Users\\arauj\\OneDrive\\Documentos\\UiPath\\testProcess",
  "restore": true,
  "analyze": true,
  "pack": false
}
```

Output:

```json
{
  "summary": "Validation completed.",
  "success": true,
  "restore": {},
  "analyze": {},
  "pack": {},
  "errors": [],
  "warnings": [],
  "recommendations": []
}
```

Acceptance:

```text
Returns structured CLI validation result
```

---

## 25. Agent Handoff Prompts

Use these prompts to delegate work to other agents with clean context.

---

### Agent 1: Scaffold Solution

```text
You are implementing the UiPath Engineering MCP server.

Read UiPath_Engineering_MCP_Implementation_Handoff.md as the execution plan.

Your task:
- Create the .NET 8 solution at C:\Users\arauj\Documents\UiPathEngineeringMCP
- Create the Server, Core, Providers, Tools, and Tests projects
- Add project references
- Add MCP SDK packages
- Add appsettings.json
- Add health endpoint
- Add MCP server skeleton
- Ensure dotnet build succeeds
- Ensure GET /health returns Healthy

Do not implement XAML parsing yet.
Do not implement Git or GitLab yet.
Use C# only.
```

---

### Agent 2: Core Models

```text
You are implementing the UiPath Engineering MCP server.

Read UiPath_Engineering_MCP_Implementation_Handoff.md as the execution plan.

Your task:
- Create configuration models
- Create UiPathProjectModel
- Create WorkflowModel
- Create PackageModel
- Create DependencyModel
- Create AssetModel
- Create QueueModel
- Create VariableModel
- Create ArgumentModel
- Create ExceptionHandlerModel
- Create InvokeWorkflowModel
- Create ToolResult

Requirements:
- Use C#
- Use .NET 8
- Make models JSON-serializable
- Do not parse XAML yet
- Do not call UiPath CLI yet
```

---

### Agent 3: Filesystem Provider

```text
You are implementing the UiPath Engineering MCP server.

Read UiPath_Engineering_MCP_Implementation_Handoff.md as the execution plan.

Your task:
- Implement IFilesystemProvider
- Implement FilesystemProvider
- Validate paths against allowed roots
- Find project.json
- Find .xaml files
- Build directory tree
- Ignore .git, .local, .settings, bin, obj, node_modules
- Read README.md if present

Test project:
C:\Users\arauj\OneDrive\Documentos\UiPath\testProcess

Requirements:
- Use C#
- Handle missing files gracefully
- Do not throw raw exceptions to tools
```

---

### Agent 4: PowerShell Provider

```text
You are implementing the UiPath Engineering MCP server.

Read UiPath_Engineering_MCP_Implementation_Handoff.md as the execution plan.

Your task:
- Implement IPowerShellProvider
- Implement PowerShellProvider
- Implement PowerShellResult
- Capture stdout
- Capture stderr
- Capture exit code
- Enforce timeout
- Support pwsh.exe with fallback to powershell.exe

Requirements:
- Use C#
- Do not allow arbitrary user-provided shell commands from MCP tools
- Do not hang indefinitely
```

---

### Agent 5: UiPath CLI Provider

```text
You are implementing the UiPath Engineering MCP server.

Read UiPath_Engineering_MCP_Implementation_Handoff.md as the execution plan.

Your task:
- Implement IUiPathCliProvider
- Implement UiPathCliProvider
- Implement UiPathCliResult
- Implement UiPathCliCommandBuilder
- Support restore, analyze, pack
- Accept either project directory or project.json path
- Use uip.exe
- Normalize output into errors, warnings, summary
- Suppress raw CLI output by default
- Enforce timeout

Known CLI examples:
uip restore "C:\Projects\MyRobot\project.json"
uip restore
uip analyze "C:\Projects\MyRobot\project.json"
uip analyze
uip pack "C:\Projects\MyRobot" --output "C:\Packages"

Test project:
C:\Users\arauj\OneDrive\Documentos\UiPath\testProcess

Requirements:
- Use C#
- Do not publish to Orchestrator yet
- Do not return secrets
- Do not return raw console output unless IncludeRawOutput is true
```

---

### Agent 6: MCP Tools

```text
You are implementing the UiPath Engineering MCP server.

Read UiPath_Engineering_MCP_Implementation_Handoff.md as the execution plan.

Your task:
- Implement AnalyzeProjectTool
- Implement ValidateProjectTool
- Register tools with MCP server
- Return structured JSON
- Use providers instead of direct file or process access
- Validate project paths

Tool inputs:
analyze_project:
{
  "projectPath": "C:\\Users\\arauj\\OneDrive\\Documentos\\UiPath\\testProcess"
}

validate_project:
{
  "projectPath": "C:\\Users\\arauj\\OneDrive\\Documentos\\UiPath\\testProcess",
  "restore": true,
  "analyze": true,
  "pack": false
}

Requirements:
- Use C#
- Do not throw raw exceptions to MCP clients
- Return structured errors
```

---

### Agent 7: Dev Tunnel and Copilot Registration

```text
You are configuring external access for the UiPath Engineering MCP server.

Read UiPath_Engineering_MCP_Implementation_Handoff.md as the execution plan.

Your task:
- Install Microsoft Dev Tunnel CLI
- Login
- Create tunnel named uipath-mcp
- Expose local port 5000
- Host the tunnel
- Verify HTTPS health endpoint
- Verify HTTPS SSE endpoint
- Document the tunnel URL
- Prepare Microsoft 365 Copilot registration details

Local server:
http://localhost:5000

Expected endpoints:
GET /health
GET /sse
POST /messages

Requirements:
- Use anonymous access only for initial testing
- Recommend Entra ID or tunnel access control for production
- Do not expose secrets
```

---

## 26. Test Project

Primary test project:

```text
C:\Users\arauj\OneDrive\Documentos\UiPath\testProcess
```

Example `analyze_project` input:

```json
{
  "projectPath": "C:\\Users\\arauj\\OneDrive\\Documentos\\UiPath\\testProcess"
}
```

Example `validate_project` input:

```json
{
  "projectPath": "C:\\Users\\arauj\\OneDrive\\Documentos\\UiPath\\testProcess",
  "restore": true,
  "analyze": true,
  "pack": false
}
```

---

## 27. Definition of Done for First Milestone

The first milestone is complete when:

```text
1. The solution builds.
2. The MCP server runs locally.
3. GET /health returns Healthy.
4. GET /sse streams SSE events.
5. Dev Tunnel exposes the server over HTTPS.
6. The tunnel health endpoint works.
7. The tunnel SSE endpoint works.
8. analyze_project returns structured JSON for testProcess.
9. validate_project returns structured JSON for testProcess.
10. Raw CLI output is suppressed by default.
11. Only allowed project roots are accepted.
12. Errors are returned as structured JSON.
```

---

## 28. Risks and Mitigations

### Risk: UiPath CLI output varies

Mitigation:

- Capture output internally.
- Normalize into errors, warnings, and summary.
- Add parser tests with sample CLI output.

---

### Risk: OneDrive file locks

Mitigation:

- Ensure files are locally available.
- Handle IO exceptions gracefully.
- Avoid writing artifacts into OneDrive.
- Use central artifacts folder.

---

### Risk: MCP SDK transport API changes

Mitigation:

- Isolate MCP hosting in Server project.
- Keep tools provider-based.
- Adjust transport mapping in one place.

---

### Risk: Copilot registration requires authentication

Mitigation:

- Start with anonymous Dev Tunnel for local validation.
- Switch to Entra ID or tunnel access control before production.

---

### Risk: Large UiPath projects

Mitigation:

- Build Internal Project Model once.
- Cache parsed model per request or per session.
- Limit folder depth.
- Limit XAML parsing depth in early milestones.

---

## 29. Recommended Next Implementation Order

```text
1. Scaffold solution
2. Add configuration models
3. Add Filesystem Provider
4. Add PowerShell Provider
5. Add UiPath CLI Provider
6. Add Internal Project Model builder
7. Implement analyze_project
8. Implement validate_project
9. Test locally
10. Expose through Dev Tunnel
11. Register in Microsoft 365 Copilot
12. Add XAML parser
13. Add dependency graph
14. Add explain_workflow
15. Add generate_documentation
```

---

## 30. Current Execution Decision

Status update (v2, 2026-07-28):

- Phases 0-2, 4, 5: done and running on Copilot Studio (MVP v1).
- Phase 3 (PowerShell Provider): deferred — CLI provider executes `uip.exe` directly.
- Hardening done: structured `UiPathCliOutputParser` (analyzer/NuGet/fallback line parsing, fixture tests), `validate_project` now returns per-step results (`executed`/`success`/`errors`/`warnings` for restore/analyze/pack) plus deterministic recommendations.
- Phase 6 done: `XamlWorkflowParser` (arguments, variables, activity outline, TryCatch, InvokeWorkflowFile, LogMessage; malformed XAML returns structured parse error). `UiPathProjectModel` enriched (Workflows as `WorkflowModel`, Packages, ReadmeSummary, aggregated sub-models).
- Phase 7 done: `DependencyGraphBuilder` (edges, cycles, orphans); risks surfaced on the model.
- Phase 8 done: `explain_workflow` and `generate_documentation` tools implemented with tests.
- FolderStructure done: `IFilesystemProvider.GetDirectoryTree(root, maxDepth)` implemented (ignore-list aware, depth-capped, tolerant of inaccessible dirs); `UiPathProjectModel.FolderStructure` populated by `ProjectModelBuilder` and serialized by `analyze_project`.
- Phase 9 done: `GitProvider` (`git status`/`log` via fixed arg templates, allowed-roots validated, never throws on non-repo) + `GitLabProvider` (REST v4 issue search/create, token header-only, never surfaced in errors); `search_repository` and `create_work_items` tools; `GitLab` config section added.
- Model caching done: `CachingProjectModelBuilder` decorator (fingerprint = file count + max LastWriteTimeUtc; per-key semaphore; stale-on-error, inner exceptions never cached) registered as singleton `IProjectModelBuilder` in Server DI.
- Remaining: Phase 3 (PowerShell Provider, still deferred — no consumer), tunnel auth hardening for production.

Proceed with the first milestone:

```text
analyze_project()
validate_project()
```

Using:

```text
C:\Users\arauj\OneDrive\Documentos\UiPath\testProcess
```

As the first real UiPath test project.
```