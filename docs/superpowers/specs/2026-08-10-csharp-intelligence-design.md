# Design: Vision-Spec Roadmap + SP1 C# Intelligence (Roslyn)

Date: 2026-08-10
Status: Approved design (pending user spec review)

## 1. Background

The UiPath Engineering MCP server exists and is at a working "v4" state: 26 tool
classes covering project analysis (`analyze_project`), CLI validation
(`validate_project`, `run_uip_cli`), workflow explanation/documentation, spec-based
XAML authoring, file read/edit, implementation plans, gap analysis, `verify_work`,
skills reading, and GitLab work items. Tests exist for Core, Providers, and Tools.

The "AI Coding Infrastructure Specification" (vision spec) describes the full target:
XAML AST intelligence, C#/Roslyn intelligence, codebase semantic search, indexing,
package metadata, knowledge base, governance rules, and task state. That vision is
too large for a single implementation cycle, so it is decomposed into ordered
sub-projects below. Each sub-project gets its own spec → plan → build cycle.

This document captures (a) the decomposition and (b) the detailed design for
sub-project 1.

## 2. Gap Map: Vision Spec vs. v4

| Vision capability | v4 status |
|---|---|
| `get_project_context` | Covered by `analyze_project` |
| `validate_project` / `analyze_project` (CLI) | Covered by `validate_project`, `run_uip_cli` |
| `get_workflow_structure` | Partially covered by `explain_workflow` (no AST, no activity IDs) |
| `modify_workflow` | Covered by spec-based authoring (`build_workflow`, `insert_activities`, `manage_workflow_data`, `edit_workflow_activity`) |
| `apply_patch` | Partially covered by `edit_workflow_file` (string replace, not unified diff) |
| Task state | Partially covered by implementation-plan tools (persistent plans, not ephemeral task state) |
| Knowledge layer | Partially covered by `list_skills` / `read_skill` |
| Governance rules | Partially covered by `ProjectGapAnalyzer` (fixed rules, not configurable/machine-readable rule sets) |
| XAML AST with stable activity IDs, `find_activity`, `get_workflow_dependencies` | **Missing** |
| C# intelligence (`find_code_symbol`, `find_code_references`, `get_code_context`, `get_compile_errors`, `compile_project`) | **Missing** — C# understanding is text-based (`CodedSourceFileParser`) |
| `search_codebase` | **Missing** (`search_repository` searches GitLab issues, not code) |
| Codebase index / incremental indexing | **Missing** (in-memory fingerprint model cache only) |
| `get_project_dependencies` / `get_activity_metadata` | **Missing** as tools (an internal `ActivityCatalog` exists for spec validation) |
| `search_uipath_knowledge` | **Missing** |

## 3. Roadmap (Sub-Projects)

- **SP1 — C# Intelligence (Roslyn).** `find_code_symbol`, `get_code_context`,
  `find_code_references`, `get_compile_errors`, `compile_project`. Replaces
  text-based C# understanding with real semantics. **Detailed in this document.**
- **SP2 — Codebase Search.** `search_codebase` (text/symbol/activity/workflow
  modes) on top of SP1's Roslyn workspace plus the existing XAML project model.
  Indexing stays incremental via the fingerprint-cache pattern; no persistent DB
  until profiling proves it necessary. The vision spec's "semantic"/embedding mode
  is deferred (YAGNI) until text+symbol modes prove insufficient.
- **SP3 — XAML Intelligence Upgrade.** Workflow AST with stable activity IDs,
  `find_activity`, `get_workflow_dependencies` as a standalone tool; upgrades
  `edit_workflow_activity` from DisplayName matching to ID addressing.
- **SP4 — Package/Activity Metadata.** `get_project_dependencies` and
  `get_activity_metadata` tools over installed NuGet packages; extends the existing
  internal `ActivityCatalog`.
- **SP5 — Validation & Governance.** Composite `validate_project` pipeline
  (structure, dependencies, XAML, compile, Workflow Analyzer, custom rules) plus
  configurable machine-readable company engineering rules; extends
  `ProjectGapAnalyzer`.
- **SP6 — Knowledge Base.** `search_uipath_knowledge` with excerpt retrieval over
  the existing skills/docs layer.

Rationale for SP1 first: the primary pain is Copilot's ability to understand and
navigate large projects. C# coded workflows are currently opaque to Copilot beyond
a text parse, and SP1's Roslyn workspace is the foundation SP2's symbol search
mode builds on.

---

# SP1: C# Intelligence (Roslyn) — Detailed Design

## 4. Approach

Roslyn runs **in-process, cached**. `Microsoft.CodeAnalysis.CSharp` is added to
`UiPath.Engineering.Mcp.Core`. A Roslyn `AdhocWorkspace` + `CSharpCompilation` is
built per project on first use and cached with the same fingerprint pattern as
`CachingProjectModelBuilder`. No `dotnet build` is needed for symbol or
diagnostic queries; `compile_project` remains an authoritative CLI build wrapper.

Rejected alternatives:

- **CLI-driven + light parsing** — `get_compile_errors` via `uip rpa build` output
  parsing and regex symbol search. Simpler, but symbol/reference quality stays
  shallow and builds are slow (seconds to minutes per call).
- **Persistent index first** — full Codebase Memory (SQLite, `register_workspace`,
  background indexing) before any tools. Most infrastructure, slowest path to
  useful Copilot capabilities; deferred to SP2 if profiling justifies it.

## 5. Architecture & Components

New code lives in `UiPath.Engineering.Mcp.Core` under `CodeAnalysis/`, following
the existing layering (Core = models/parsing, Providers = external processes,
Tools = thin MCP classes):

- **`CSharpWorkspaceBuilder`** — constructs the `AdhocWorkspace` +
  `CSharpCompilation` for a UiPath project: collects `.cs` files via the existing
  filesystem discovery (skips `bin`/`obj`/`.git`/`.local`), parses `project.json`
  for dependencies and target framework, and attaches resolved metadata references.
- **`NuGetReferenceResolver`** — maps `project.json` dependencies to assemblies in
  the NuGet global-packages folder, plus framework reference assemblies from the
  machine's .NET targeting packs (Section 8).
- **`CSharpAnalysisCache`** — caches the built `Compilation` per project path,
  keyed by a fingerprint (`.cs` file count + newest `.cs` write time +
  `project.json` write time). Rebuilds only when the fingerprint changes.
  Independent of the existing project-model cache; no coupling to
  `analyze_project`.
- **`CSharpAnalysisService`** — the query API tools call: find symbol, find
  references, get context, get diagnostics. Stateless beyond the cache; all
  methods take `projectPath`.
- **Five new tool classes** in `UiPath.Engineering.Mcp.Tools` — thin wrappers:
  validate path against `Projects:AllowedRoots`, call the service, shape JSON,
  structured errors via the existing `ToolErrorCodes` pattern.

Explicitly out of scope for SP1:

- Replacing `CodedSourceFileParser` inside `analyze_project` (it keeps working;
  consolidation is a later decision).
- Persistent index/DB, embeddings, any XAML changes, `search_codebase`.

## 6. Tool Surface

All five tools are read-only from the MCP client's perspective.

| Tool | Input | Output (shaped JSON) |
|------|-------|----------------------|
| `find_code_symbol` | `projectPath`, `symbol`, optional `kind` | matches: `name`, `kind`, `file`, `line`, `containingType`, `signature` |
| `get_code_context` | `projectPath`, `symbol` **or** `file`+`line` | containing type, method signature, parameters, return type, called methods, referenced types, bounded source excerpt (enclosing member only) |
| `find_code_references` | `projectPath`, `symbol` | reference locations: `file`, `line`, `containingMember`, one-line snippet |
| `get_compile_errors` | `projectPath`, optional `severity` | diagnostics: `file`, `line`, `column`, `code`, `severity`, `message` |
| `compile_project` | `projectPath` | authoritative build via existing `UiPathCliProvider` (`uip rpa build`), normalized per-step result in the `validate_project` shape conventions |

Every Roslyn-backed response carries:

- `analysisMode`: `"full"` | `"partial"` | `"syntaxOnly"`
- `unresolvedReferences`: list of dependency IDs whose assemblies could not be
  resolved (present when not `"full"`)

`get_code_context` honors the minimal-context principle: it returns the enclosing
member's source, not whole files.

## 7. Data Flow

All four Roslyn-backed tools follow the same pipeline:

```text
tool call
  → path validated against Projects:AllowedRoots (existing guard)
  → CSharpAnalysisService.GetCompilationAsync(projectPath)
       → CSharpAnalysisCache fingerprint hit? return cached Compilation
       → miss → CSharpWorkspaceBuilder (+ NuGetReferenceResolver) → cache → return
  → Roslyn query (symbol lookup / FindReferences / GetDiagnostics / context)
  → shaped JSON (no raw exceptions; ToolErrorCodes pattern)
```

`compile_project` skips Roslyn and goes straight to the CLI provider, reusing the
existing timeout, executable-resolution, and raw-output-suppression plumbing.

## 8. Reference Resolution & Degradation

A UiPath project has no `.csproj`; compilation inputs come from `project.json`
(`dependencies`, `targetFramework`). `NuGetReferenceResolver` works in this order:

1. **Package folder** — `%NUGET_PACKAGES%`, else `%USERPROFILE%\.nuget\packages`.
   For each dependency: `<id>/<version>/`, prefer `ref/<tfm>/` over `lib/<tfm>/`,
   pick the nearest compatible TFM folder (exact, then lower compatible — e.g.
   `net6.0` accepts `netstandard2.0`). If the exact version folder is absent, take
   the highest installed version and record the dependency in
   `unresolvedReferences`.
2. **Framework assemblies** — modern targets (`net6.0`, `net8.0-windows`): .NET
   targeting packs under `%ProgramFiles%\dotnet\packs\Microsoft.NETCore.App.Ref`;
   fallback to the server's own .NET 8 runtime assemblies. Legacy (`net461`):
   Windows reference-assemblies folder if present; otherwise degrade.
3. **Degradation tiers** drive `analysisMode`:
   - `full` — all dependencies and framework assemblies resolved; all tools fully
     semantic.
   - `partial` — some package assemblies missing. Symbol/context/reference tools
     still work for declared symbols. `get_compile_errors` suppresses the known
     missing-reference noise codes (`CS0234`, `CS0246`, `CS0012`) so Copilot sees
     actionable errors, and reports that suppression in the response.
   - `syntaxOnly` — NuGet folder unreachable. Declaration lookup by syntax still
     works; reference finding falls back to identifier-name matching across syntax
     trees; diagnostics are omitted with an explanatory note.

Boundaries and performance:

- Only `.cs` files inside the project are compiled. XAML workflows are out of
  scope (Studio compiles those itself).
- First compilation of a typical project is expected around 1–2 s; subsequent
  calls are served from the cache. No background threads — built lazily on first
  tool call.

## 9. Error Handling

Follows the existing `ToolError`/`ToolErrorCodes` contract; no raw exceptions
reach the MCP client.

- Reused categories: path not allowed, `project.json` not found, CLI not found /
  CLI timeout (`compile_project`, via existing provider plumbing).
- New categories:
  - `no_csharp_files` — project has no `.cs` files. Query tools return empty
    results with a note (not an error); the information is still useful to Copilot.
  - `workspace_build_failed` — an individual file fails to parse. The offending
    file is excluded, diagnostics for the remaining files are still returned, and
    a warning is attached.
- Cancellation flows from the MCP request into Roslyn queries
  (`CancellationToken`) and into CLI timeouts as today.
- The cache stores immutable `Compilation` instances; concurrent tool calls need
  no locking beyond the cache dictionary.

## 10. Testing

xUnit, hand-written fakes, no Moq (existing convention).

- **Core.Tests** — temp-dir fixture projects: a coded workflow with known
  symbols; a project with a deliberate `CS0103`; a fake NuGet package folder to
  drive the `full`/`partial`/`syntaxOnly` tiers. Covers: workspace builder
  (discovery skips `bin`/`obj`/`.local`), resolver TFM/version selection, cache
  fingerprint invalidation, symbol find / references / context bounding,
  diagnostics shaping, and partial-mode noise suppression.
- **Tools.Tests** — all five tools: path-not-allowed, `project.json` missing,
  happy-path output shapes, `analysisMode` present in every response,
  `compile_project` success/failure/CLI-not-found via the existing fake
  `IUiPathCliProvider` pattern.
- One opt-in integration test against the real NuGet folder, auto-skipped when
  absent.

## 11. Acceptance Criteria

- All five tools listed in the MCP Inspector against the local server.
- Against the primary test project, `find_code_symbol` locates a coded-workflow
  entry method with correct file/line/signature, and `find_code_references`
  returns its call sites.
- `get_compile_errors` on a fixture project with a deliberate `CS0103` returns the
  structured diagnostic with correct file/line/column/code.
- With a fake NuGet folder missing one dependency, responses report
  `analysisMode: "partial"` and name the unresolved dependency.
- With no NuGet folder, responses report `analysisMode: "syntaxOnly"` and symbol
  declaration lookup still works.
- A second `find_code_symbol` call after a `.cs` edit reflects the change (cache
  invalidation), and an unchanged project is served from cache.
- `dotnet test` passes across all three test projects.
