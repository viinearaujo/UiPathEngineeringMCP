# Design: SP3 — XAML Intelligence

Date: 2026-08-11
Status: Approved design (pending user spec review)

## 1. Background

The UiPath Engineering MCP server is at SP2-complete state: SP1 delivered Roslyn-backed
C# intelligence (`find_code_symbol`, `get_code_context`, `find_code_references`,
`get_compile_errors`, `compile_project`); SP2 delivered `search_codebase` with
text/symbol/activity/workflow modes. SP1's design doc
(`docs/superpowers/specs/2026-08-10-csharp-intelligence-design.md`, section 3) defined
the roadmap; this document is the detailed design for SP3.

SP3 per the roadmap: workflow AST with stable activity IDs, `find_activity`,
`get_workflow_dependencies` as a standalone tool, and upgrading
`edit_workflow_activity` from DisplayName matching to ID addressing.

Current state (verified against the code):

- `XamlWorkflowParser` flattens activities into `ActivityModel { DisplayName, Type,
  Depth }`. No IDs, no parent/child links, no document order, no line numbers.
- `XamlActivityEditor` locates edit targets by exact DisplayName match (optionally
  narrowed by type); ambiguity fails with "make display names unique".
- `DependencyGraphBuilder` computes the project-wide InvokeWorkflowFile graph
  (edges/cycles/orphans) but is not exposed as a tool; per-workflow callers are not
  precomputed; `InvokeWorkflowModel` does not record argument mappings.
- SP2's `SearchActivitiesAsync` already anticipates this work: "line-level activity
  addressing lands with SP3."

Key constraint: UiPath XAML has no native per-activity IDs, so IDs must be computed,
and structural edits shift positions — IDs are per-parse-snapshot, not durable across
edits.

## 2. Approach

Computed structural-path IDs with an additive model upgrade. Rejected alternatives:

- **Hash-slug IDs** (`act_7f3a2b`) — equally stable but opaque; neither Copilot nor a
  human can sanity-check an ID against the workflow. Rejected.
- **Persistent IDs injected into XAML** (marker attributes/annotations) — durable
  across edits but mutates user files and risks Studio compatibility. Rejected by the
  user.
- **Separate AST model alongside the flat model** — two parallel representations of
  the same XAML to keep in sync. Rejected by the user; `ActivityModel` is extended
  additively instead.
- **Replace flat list with tree** — touches all five current consumers
  (`explain_workflow`, `search_codebase`, gap analyzer, docs, dependency graph) and
  their tests. Rejected as disproportionate.

## 3. ID Scheme

Every activity element gets a deterministic structural-path ID:

```text
<localName>.<ordinal> segments joined by "/"
```

- `localName` is the XAML element local name, lowercased (e.g. `sequence`, `if`,
  `logmessage`).
- `ordinal` is 1-based in document order among **all** activity siblings under the
  same parent (not per-name) — e.g. a Sequence containing If then LogMessage yields
  `if.1` and `logmessage.2`.
- Attached-property containers (`Sequence.Variables`, `TryCatch.Catches`, any
  dot-suffixed local name) and the shared `NonActivityElements` classification remain
  transparent — recursed without consuming a path segment. This reuses the exact
  classification already shared by parser and editor.

Example: `sequence.1/if.1/logmessage.2`.

Properties: deterministic for identical file content; human-readable; stable across
cache hits and repeated reads. IDs are **per-parse-snapshot** — structural edits
(insert/remove/replace) may shift ordinals after the edit point. This is documented
in tool outputs and handled by stale-ID verification in the editor (Section 5).

## 4. Model & Parser Changes (Core)

### ActivityModel (additive)

New fields alongside the existing `{ DisplayName, Type, Depth }`:

```csharp
public string Id { get; init; } = string.Empty;
public string? ParentId { get; init; }        // null for root activities
public int Order { get; init; }               // document-order index
public int Line { get; init; }                // 1-based line in the .xaml file
[JsonIgnore]
public List<ActivityModel> Children { get; init; } = [];
```

`WorkflowModel.Activities` stays the flat pre-order list — existing consumers keep
working and their serialized output only gains the new scalar fields. `Children` is
`[JsonIgnore]`: in-memory navigation only, so `analyze_project` / `explain_workflow`
responses do not double in size. Hierarchy is reconstructible from `Id`/`ParentId`;
tools that need the tree serialize explicit DTOs (Section 5).

### InvokeWorkflowModel (additive)

```csharp
public List<ArgumentMappingModel> ArgumentMappings { get; init; } = [];
```

`ArgumentMappingModel { Direction, TargetArgument, Expression }` extracted from the
`InvokeWorkflowFile.Arguments` dictionary child element (`InArgument`/`OutArgument`
entries with `x:Key` and the binding expression).

### Parser changes

- `XamlWorkflowParser` parses with `LoadOptions.SetLineInfo` (line numbers) in
  addition to current behavior.
- The recursive `Walk` is extracted into a shared **`XamlActivityLocator`**
  (Core/Parsing): given an `XDocument`, yields
  `(element, id, parentId, order, line, depth)` per activity. Both the parser and
  `XamlActivityEditor` use it — one traversal, one ID assignment, no drift between
  what `find_activity` reports and what the editor edits.
- Argument-mapping extraction is added next to the existing InvokeWorkflowFile
  handling.
- Malformed XAML keeps returning structured parse errors (`HasParseError`/
  `ParseError`); never throws.

`XamlBuilder.RenderWorkflowFile` round-trips generated XAML through the parser; the
additive model keeps that path (and `ProjectModelBuilder` aggregation) intact.

## 5. Tool Surface

All tools validate paths against `Projects:AllowedRoots` and return structured JSON
via the existing `ToolResult`/`ToolError` pattern.

### New: `find_activity`

| Input | |
|---|---|
| `projectPath` | required |
| `workflowFile` | optional; limits search to one workflow |
| `query` | optional DisplayName substring (case-insensitive) |
| `activityType` | optional exact type filter |
| `activityId` | optional exact-ID lookup |

Backed by the cached `UiPathProjectModel` (existing `CachingProjectModelBuilder`;
fingerprint invalidation on edits comes free). Output per match:

```json
{
  "id": "sequence.1/if.1/logmessage.2",
  "displayName": "Log start",
  "type": "LogMessage",
  "workflowFile": "Main.xaml",
  "line": 42,
  "parentId": "sequence.1/if.1",
  "depth": 2,
  "ancestors": [
    { "id": "sequence.1", "displayName": "Main Sequence" },
    { "id": "sequence.1/if.1", "displayName": "If connected" }
  ]
}
```

Empty result is success-with-note (SP1 `no_csharp_files` convention), not an error.

### New: `get_workflow_dependencies`

| Input | |
|---|---|
| `projectPath` | required |
| `workflowFile` | optional; per-workflow mode when present |

- **Per-workflow mode:** `callers` and `callees` for the named workflow; each edge
  carries the InvokeWorkflowFile argument mappings.
- **Project-wide mode** (no `workflowFile`): full edge list plus `cycles`,
  `orphans`, and `unresolved` edges — data that today only surfaces as risk strings
  in `analyze_project`.

`DependencyGraphBuilder` gains an incoming-edge (callers) index; mapping data comes
from the enriched `InvokeWorkflowModel`.

### Upgraded: `edit_workflow_activity` and `insert_activities`

- New preferred input `activityId` (target activity / target container).
  `displayName` (+`activityType`) retained for back-compat.
- ID resolution re-parses the current file content through the shared
  `XamlActivityLocator`, then **verifies** the resolved element's type (and
  displayName when also supplied). A stale ID — file edited since the snapshot —
  fails with `ACTIVITY_ID_STALE` instead of editing the wrong element.
- Success responses include the affected activity's ID and a note that IDs after the
  edit point may have shifted.
- Both tools move from plain string failures to the structured
  `ToolError`/`ToolErrorCodes` pattern used by the newer tools.

### Additive enrichment (no new tools)

- `search_codebase` activity-mode hits gain `id` and `line` (fulfills the SP2 note).
- `explain_workflow` gains an optional `includeActivityTree` flag that nests
  activities as a tree DTO — the one place the AST hierarchy is directly serialized.

## 6. Error Handling

Existing `ToolError`/`ToolErrorCodes` contract; no raw exceptions reach the MCP
client.

New codes:

- `ACTIVITY_NOT_FOUND` — ID or DisplayName resolves to nothing.
- `ACTIVITY_ID_STALE` — ID resolves but type/displayName verification fails;
  fix hint: re-run `find_activity`.
- `AMBIGUOUS_ACTIVITY` — DisplayName matches more than one activity;
  fix hint: pass `activityId`.

Reused: path not allowed, file not found, XAML parse failure (structured, as today).

## 7. Testing

xUnit, hand-written fakes, inline XAML literals — existing conventions.

- **Core.Tests** — locator/parser: ID determinism (same content → same IDs),
  hierarchy correctness through transparent attached-property containers, line
  numbers, argument-mapping extraction; `DependencyGraphBuilder` callers index;
  editor: stale-ID detection, edit-by-ID insert/replace/remove round-trips.
- **Tools.Tests** — `find_activity` filter combinations and output shape;
  `get_workflow_dependencies` per-workflow and project-wide shapes; edit tools
  accepting `activityId`; structured error codes on stale/ambiguous/not-found;
  `search_codebase` activity hits carrying `id`/`line`.

## 8. Acceptance Criteria

- `find_activity` returns stable IDs and line numbers against the primary test
  project (`C:\Users\arauj\OneDrive\Documentos\UiPath\testProcess`); a repeated call
  on an unchanged file returns identical IDs.
- `get_workflow_dependencies` on the test project shows Main.xaml's callees with
  argument mappings, and callers for a child workflow.
- `edit_workflow_activity` edits by `activityId`; after a structural edit, the old
  ID is rejected with `ACTIVITY_ID_STALE`.
- `explain_workflow` / `analyze_project` / `search_codebase` outputs are unchanged
  except the additive fields.
- `dotnet test` passes across all three test projects.

## 9. Out of Scope (YAGNI)

- Persistent/injected XAML IDs.
- Variable read/write data-flow analysis within workflows.
- XAML formatting preservation beyond the editor's current whitespace handling.
- Semantic/embedding search.
