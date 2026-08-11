# SP2 — Codebase Search: Design

Date: 2026-08-11
Status: Approved (design)
Parent roadmap: `docs/superpowers/specs/2026-08-10-csharp-intelligence-design.md` (§3, SP2)

## 1. Goal

Give Copilot Studio a single `search_codebase` tool that answers "where is X
generated / used" across a UiPath project's `.xaml` and `.cs` files without
reading raw files. Four modes in v1: `text`, `symbol`, `activity`, `workflow`.

Explicitly out of scope (settled, do not reopen):

- Persistent index/DB, embeddings, "semantic" mode — deferred as YAGNI.
- Cross-project or multi-path search — per-project only, matching SP1's
  `projectPath` pattern. Copilot issues multiple calls for multi-project questions.
- Dependency mode — deferred; SP4 owns package/activity metadata.
- Line-level activity addressing — the current `ActivityModel` has no line
  numbers; SP3's XAML AST adds that. SP2 activity hits locate the workflow file.
- Text-mode file scope beyond `.xaml` + `.cs` (no `project.json`, no docs/configs).
- The known `CSharpAnalysisCache` fingerprint gap (ignores NuGet-folder changes
  after `dotnet restore`): inherited by symbol mode, flagged as a candidate fix,
  not part of SP2.

## 2. Settled Decisions (from brainstorming)

| Question | Decision |
|----------|----------|
| v1 modes | All four: text, symbol, activity, workflow |
| Search scope | One project per call (`projectPath` required) |
| Text-mode files | `.xaml` + `.cs` only (via existing finders; bin/obj/.local excluded) |
| Ranking | Deterministic order; no relevance floats |
| Result size | Fixed cap `MaxResults = 200` (SP1 constant) + `truncated` flag/note |
| Result shape | Per-mode typed matches over a shared base envelope |
| Matching semantics | Case-insensitive substring; exact case-sensitive hits order first |
| Text implementation | Core-side line scan via existing `IFilesystemProvider` (`FindXamlFiles`/`FindCSharpFiles`/`ReadAllText`); no provider interface change |

Rejected alternatives for text search:

- **Provider-side search method** — would churn every `IFilesystemProvider`
  implementation and its tests, and leak search logic out of Core (against the
  "Core holds logic" layering).
- **Persistent text index** — deferred as YAGNI by the roadmap.

## 3. Architecture & Components

New code lives in `src/UiPath.Engineering.Mcp.Core/CodeSearch/`, mirroring
SP1's `CodeAnalysis/` layout (Core = logic, Tools = thin MCP wrappers):

- **`ICodebaseSearchService` / `CodebaseSearchService`** — one method per mode:
  `SearchTextAsync`, `SearchSymbolsAsync`, `SearchActivitiesAsync`,
  `SearchWorkflowsAsync`, each taking `(projectPath, query, cancellationToken)`.
  Stateless beyond injected caches. Dependencies (all existing, all cached):
  - `ICSharpContextBuilder` — SP1's cached `CSharpCompilation` (symbol mode).
  - `IProjectModelBuilder` — cached `UiPathProjectModel` (activity/workflow modes).
  - `IFilesystemProvider` — file enumeration + `ReadAllText` (text mode).
- **`CodebaseSearchDtos.cs`** — per-mode result types sharing a base envelope
  `CodebaseSearchResult` (`truncated`, `note`, `warnings`), plus mode-specific
  transparency fields.
- **`SearchCodebaseTool`** in `UiPath.Engineering.Mcp.Tools` — thin wrapper:
  `ToolResults.GuardProject` → dispatch on `mode` → `ToolResults.Ok` /
  `FromException`. DI registration follows the SP1 pattern in the Server project.

Symbol-mode mechanics: `Compilation.GetSymbolsWithName` only does exact-name
lookup, so substring search enumerates symbols recursively from
`Compilation.GlobalNamespace` (source-only, like SP1) and filters on name.
Enumeration cost is acceptable at UiPath project scale; the measure-later
perf stance from the roadmap stands.

## 4. Tool Surface

One tool, read-only:

```
search_codebase(projectPath, query, mode, kind? = null)
```

- `projectPath` — absolute path to the UiPath project directory; guarded
  against `Projects:AllowedRoots` (same as every SP1 tool).
- `query` — search text. Case-insensitive substring in all modes;
  exact case-sensitive hits order first.
- `mode` — `text | symbol | activity | workflow`. Unknown values →
  `INVALID_ARGUMENT` listing valid modes (existing `ToolErrorCodes` pattern).
- `kind` — optional, symbol mode only: `method | property | field | class |
  interface` (reuses SP1's `KindMatches` semantics).

Output: `ToolResults.Ok` envelope — summary line first (e.g. `Found 12 text
match(es) for 'queue' across 5 file(s).`), then the shaped per-mode DTO,
warnings attached when present.

Tool description positions the tool against SP1's: `search_codebase` =
fuzzy/substring discovery across `.xaml` and `.cs`;
`find_code_symbol`/`find_code_references` = exact-name semantic lookup in
`.cs` only.

## 5. Per-Mode Behavior & Result Shapes

All modes cap at `MaxResults = 200` (the SP1 constant); overflow sets
`truncated: true` plus a note telling Copilot to narrow the query.

Base envelope `CodebaseSearchResult`: `{ truncated, note, warnings }`.

### text

Scans every file from `FindXamlFiles` + `FindCSharpFiles` (bin/obj/.local
already excluded by the finders). Per-file guard: files over 2 MB are skipped
and listed in `skippedFiles`. Matching: case-insensitive substring per line.

- Match item: `{ filePath, line, snippet }` — `snippet` is the trimmed line
  capped at 300 chars with ellipsis (XAML lines run long).
- Extra result fields: `filesSearched`, `skippedFiles`.

### symbol

Source-only symbols from the cached compilation, name contains query
(case-insensitive). `kind` filter applies.

- Match item: SP1's `SymbolMatch` shape `{ name, kind, filePath, line,
  containingType, signature }`.
- Envelope adds SP1 transparency fields: `analysisMode`,
  `unresolvedReferences`, `hasCSharpFiles`.

### activity

Over the cached `UiPathProjectModel`; matches when `DisplayName` **or**
`Type` contains the query (case-insensitive).

- Match item: `{ workflowFile, workflowPath, displayName, activityType, depth }`.
- A note states the limitation: no line numbers on activities in v1 — hits
  locate the workflow file; line-level addressing lands with SP3.

### workflow

Matches workflow `FileName` or `Description` contains query (case-insensitive).

- Match item: `{ fileName, filePath, isMain, description, matchedOn }` where
  `matchedOn` is `name | description | both`.

### Ordering (all modes)

1. Exact case-sensitive hits first (full-name equality for
   symbol/activity/workflow names; case-sensitive substring for text lines).
2. Then case-insensitive-only matches.
3. Then stable by file path + line.

No relevance floats.

## 6. Degradation, Transparency & Error Handling

- **Symbol mode** inherits SP1's degradation wholesale: `analysisMode` =
  full/partial/syntaxOnly with `unresolvedReferences` populated. Source-symbol
  enumeration still works in degraded modes (signatures may degrade, exactly as
  SP1's `find_code_symbol` today). The known `CSharpAnalysisCache` fingerprint
  gap (NuGet-folder changes) flows through; candidate fix, out of SP2 scope.
- **Text mode** never fails on one bad file: a `ReadAllText` IO failure adds
  the file to `skippedFiles` plus a warning; the scan continues.
- **Activity/workflow modes**: workflows with `HasParseError` are skipped for
  activity matching but still name-matchable in workflow mode; a note reports
  `N workflow(s) failed to parse`.
- **Input errors**: invalid/outside-root `projectPath` → existing
  `ToolResults.GuardProject` structured error; unknown `mode` or blank `query`
  → `INVALID_ARGUMENT` listing valid modes; anything else →
  `ToolResults.FromException`.
- Read-only end to end; no new runtime dependencies.

## 7. Testing

xUnit with hand-written fakes (no Moq), tests next to their SP1 counterparts:

- **Core tests** (`UiPath.Engineering.Mcp.Core.Tests/CodeSearch/`):
  - Text mode: in-memory fake `IFilesystemProvider` (file map with .xaml/.cs
    content) — CI matching, exact-case-first ordering, snippet trim/300-cap,
    oversized-file skip, IO-failure skip, `filesSearched`/`skippedFiles`
    accounting, 200-cap + `truncated`.
  - Symbol mode: reuse SP1's fixture pattern for building a compilation from
    source strings — substring matching, `kind` filter, exact-name-first
    ordering, degraded-mode transparency fields.
  - Activity/workflow modes: fake `IProjectModelBuilder` returning a hand-built
    `UiPathProjectModel` — DisplayName/Type matching, depth pass-through,
    parse-error note, `matchedOn` values, ordering.
- **Tools tests** (`UiPath.Engineering.Mcp.Tools.Tests`): guard failure,
  unknown-mode `INVALID_ARGUMENT`, per-mode dispatch, `Ok` envelope shape,
  `FromException` path. Registration mirrors SP1's five tools.

## 8. Definition of Done

Approved spec → approved plan → implementation via chosen execution mode →
full suite green on the merged tree (450 existing + new) → README tool
count/table updated. Copilot can answer "where is X generated / used" across
`.xaml` and `.cs` without reading raw files.
