# Design: RPA Comprehension Canvas

Date: 2026-09-25
Status: Approved design

## 1. Background

An RPA developer opening an unfamiliar UiPath project needs a readable map of
workflows and what each one does. The Engineering MCP already exposes the facts
(`analyze_project`, `get_workflow_dependencies`, `explain_workflow`). Copilot can
write prose. Neither belongs inside a browsing UI that re-calls the model.

This design splits the work: the MCP writes a deterministic fact skeleton into
the UiPath project; Copilot fills explanations afterward; a separate local app
opens the committed snapshot and never calls the MCP or Copilot.

## 2. Approach

Snapshot file plus separate local app. Rejected alternatives:

- **App served by this MCP** — couples browsing to a running tunnel and keeps
  the server in the UI path. Rejected.
- **LLM transcribes graph facts into the snapshot** — edges, arguments, and
  flags would be re-serialized by Copilot through `manage_project_file`, which
  invites hallucination and truncation on REFramework-sized projects. Rejected.
- **Live Copilot calls while browsing** — every click would depend on the tunnel
  and model latency. Rejected; every explanation is written before the canvas
  opens.
- **Activity-level or logic-block map** — grain is one node per workflow file.
  Rejected for this release.

Confirmed decisions:

| Decision | Choice |
|---|---|
| Reader | RPA developer opening a project they do not know yet |
| Grain | One node per workflow file; the map is the invoke graph |
| Timing | Explanations written before the canvas opens; browsing never calls Copilot |
| Author | Copilot, through this server, following a generation guide |
| Shape | `<uipath-project>/.canvas/snapshot.json` plus a separate local app with a file picker |

Architecture:

```text
Developer
  -> Copilot (generation guide attached)
  -> Dev Tunnel
  -> Engineering MCP
       generate_documentation(format: "canvasSnapshot")
         writes fact skeleton to <project>/.canvas/snapshot.json
       Copilot fills explanation + decisions via manage_project_file edit
  -> Canvas app (file picker opens snapshot.json)
  -> Developer
```

This MCP stays a deterministic tool server. The canvas app does not call the MCP
and does not parse XAML to fill gaps. The snapshot is committed with the UiPath
project so another developer can open the canvas without a tunnel.

## 3. MCP change (exactly one)

The developer must enable `generate_documentation` (`ToolSurface=All`, or toggle it on in Copilot Studio) for the canvas skeleton pass — it is not on the Copilot default connector.

`generate_documentation` gains an optional `format` argument. Allowed values:

| Value | Behavior |
|---|---|
| omitted / default | Existing documentation data payload (unchanged) |
| `canvasSnapshot` | Writes the fact skeleton to `.canvas/snapshot.json` and returns a short success summary |

No other new tool. Copilot does not invent nodes, edges, arguments, or flags.

### Skeleton writer (`format: "canvasSnapshot"`)

Uses the project model and `DependencyGraphBuilder` (same sources as
`analyze_project` summary, `get_workflow_dependencies` project-wide mode, and
`explain_workflow` per file). Writes `<project>/.canvas/snapshot.json` with:

- Root `schemaVersion` `1`, `generatedAt` (ISO-8601 UTC), `generator.mcpVersion`
  (the server version from `McpServer:Version` / host info).
- `project.name`, `project.entryPoint`, `project.additionalEntryPoints`, and
  `project.overview` set to `""`.
- One node per XAML workflow plus each coded file whose
  `CodedFileKind` is `workflow`. Coded `source` and `test` files are omitted.
- Per node: `id`, `kind`, `arguments`, `hasExceptionHandler`, `parseError`,
  `sha256`, `explanation` `""`, `decisions` `[]`.
- Edges from the project-wide InvokeWorkflowFile graph only.
- Absolute machine paths never written.

`sha256` is the lowercase hex SHA-256 of the workflow file bytes at generation
time. The skeleton writer computes it; Copilot must not change it.

### Expression redaction

`get_workflow_dependencies` today returns `argumentMappings[].expression` without
passing it through `SecretRedactor` (unlike `read_workflow_file`, which calls
`SecretRedactor.Redact` on file content). The skeleton writer **must** run each
mapping `expression` through `SecretRedactor.Redact` and store the returned
text before writing it into the snapshot. The committed snapshot must not carry
unredacted credential-looking values from invoke bindings.

### Return value

Success summary naming the written path, node count, edge count, and
`generatedAt`. The full snapshot is on disk, not in the tool payload.

## 4. Snapshot schema

Path: `<uipath-project>/.canvas/snapshot.json`. The canvas repo owns the JSON
Schema and publishes it next to the generation guide. `schemaVersion` is `1`.

```json
{
  "schemaVersion": 1,
  "generatedAt": "2026-09-25T20:00:00Z",
  "generator": {
    "mcpVersion": "1.2.3"
  },
  "project": {
    "name": "MyProcess",
    "entryPoint": "Main.xaml",
    "additionalEntryPoints": ["CodedEntry.cs"],
    "overview": ""
  },
  "nodes": [
    {
      "id": "Main.xaml",
      "kind": "xaml",
      "sha256": "a591a6d40bf420404a011733cfb7b190d62c65bf0bcda32b57b277d9ad9f146e",
      "arguments": [
        { "name": "in_Config", "direction": "In", "type": "Dictionary<String,Object>" }
      ],
      "hasExceptionHandler": true,
      "parseError": null,
      "explanation": "",
      "decisions": []
    }
  ],
  "edges": [
    {
      "sourceWorkflow": "Main.xaml",
      "targetWorkflow": "Framework/InitAllSettings.xaml",
      "displayName": "Init All Settings",
      "isResolved": true,
      "argumentMappings": [
        {
          "direction": "In",
          "targetArgument": "in_Config",
          "expression": "[Config]"
        }
      ]
    }
  ]
}
```

### Root

| Field | Type | Writer | Source / rule |
|---|---|---|---|
| `schemaVersion` | number | skeleton | Constant `1` |
| `generatedAt` | string | skeleton | ISO-8601 UTC when the skeleton is written |
| `generator.mcpVersion` | string | skeleton | Host MCP version (`McpServer:Version`) |

### `project`

| Field | Type | Writer | Source / rule |
|---|---|---|---|
| `name` | string | skeleton | `UiPathProjectModel.ProjectName` / `analyze_project` summary `ProjectName` |
| `entryPoint` | string \| null | skeleton | `project.json` `main` (`MainWorkflow`). Null when `main` is missing or blank (libraries). When present: `WorkflowPath.NormalizeRef(main)`, then the matching node `id` if that file is on the map; otherwise the normalized string. Header always shows this value when non-null; the map marks an entry node only when some `nodes[].id` equals it |
| `additionalEntryPoints` | string[] | skeleton | `project.json` `entryPoints[].filePath` (`EntryPoints`), each `NormalizeRef`'d (and remapped to a node `id` when that file is on the map), excluding any value equal to `entryPoint` (case-insensitive). Order preserved from `project.json` |
| `overview` | string | Copilot | Empty string from the skeleton; Copilot fills once |

### `nodes[]`

| Field | Type | Writer | Source / rule |
|---|---|---|---|
| `id` | string | skeleton | XAML: `WorkflowPath.Identity` (project-relative `/`-normalized path, same as `get_workflow_dependencies` `sourceWorkflow` / `targetWorkflow` and `analyze_project` `WorkflowIndex[].RelativePath`). Coded workflow: `WorkflowPath.ToRelativePath(projectPath, filePath)` |
| `kind` | `"xaml"` \| `"coded"` | skeleton | `"xaml"` for `.xaml`; `"coded"` only when `CodedWorkflowModel.Kind == "workflow"` |
| `sha256` | string | skeleton | Lowercase hex SHA-256 of the file bytes at skeleton time |
| `arguments` | object[] | skeleton | XAML: `WorkflowModel.Arguments` → `{ name, direction, type }` from `Name` / `Direction` / `Type`. Coded: `EntryArguments` with the same shape |
| `hasExceptionHandler` | boolean | skeleton | XAML: `true` when `ExceptionHandlers` contains any entry with `HasGlobalHandler` true (from TryCatch extraction). Coded: `true` when `EntryHasTryCatch == true`; otherwise `false` |
| `parseError` | string \| null | skeleton | When `HasParseError` is true, the `ParseError` string; otherwise `null` |
| `explanation` | string | Copilot | `""` from skeleton; Copilot fills |
| `decisions` | string[] | Copilot | `[]` from skeleton; Copilot fills with DisplayNames of decision activities (section 6) |

Duplicate `id` values are a skeleton bug; the app refuses such files (section 8).

Cycles and orphans are **not** stored. The app derives them from `edges` (section 7).

Activity trees, variables, log messages, and absolute `FilePath` values stay out of
the snapshot. Copilot may call `explain_workflow` (including
`includeActivityTree`) while writing `explanation` and `decisions`.

### `edges[]`

Copied from project-wide `get_workflow_dependencies` / `DependencyGraphBuilder`
edges. Field names match the tool JSON:

| Field | Type | Writer | Source |
|---|---|---|---|
| `sourceWorkflow` | string | skeleton | Edge source identity |
| `targetWorkflow` | string | skeleton | Resolved target identity, or normalized unresolved target string |
| `displayName` | string | skeleton | InvokeWorkflowFile DisplayName |
| `isResolved` | boolean | skeleton | `IsResolved` |
| `argumentMappings` | object[] | skeleton | `{ direction, targetArgument, expression }` from `ArgumentMappingModel`; each `expression` redacted via `SecretRedactor.Redact` |

Only XAML `InvokeWorkflowFile` edges exist today. A coded workflow with no such
link is a node with no connectors.

### Known limitation

`get_workflow_dependencies` emits only XAML InvokeWorkflowFile edges, so
coded-to-coded calls do not appear and a coded-first project renders mostly as
the unconnected group. This design does not invent coded edges.

## 5. Who writes what

| Content | Writer |
|---|---|
| Fact skeleton (project name/entry points, nodes, edges, arguments, flags, sha256, generatedAt, mcpVersion) | `generate_documentation(format: "canvasSnapshot")` |
| `project.overview` | Copilot via `manage_project_file` edit |
| Each node's `explanation` and `decisions` | Copilot via `manage_project_file` edit, batches of about 10 nodes |
| Cycles / orphans | Neither — app computes at load time |

A node may keep an empty `explanation` when generation stops early. The graph
still opens.

## 6. Generation guide

The canvas repo ships a generation guide the developer attaches in Copilot chat.
The guide is normative for Copilot behavior. Required content:

1. **Call the skeleton writer first.** Run
   `generate_documentation` with `format: "canvasSnapshot"` on the target
   project. Do not hand-build `.canvas/snapshot.json`.
2. **Do not rewrite facts the skeleton already wrote.** Never change `id`,
   `kind`, `sha256`, `arguments`, `hasExceptionHandler`, `parseError`, edges,
   `schemaVersion`, `generatedAt`, `generator`, `project.name`,
   `project.entryPoint`, or `project.additionalEntryPoints`. Only edit
   `project.overview`, `nodes[].explanation`, and `nodes[].decisions`.
3. **Write the overview once.** Set `project.overview` to a short plain-language
   summary of what the process does and how work flows from the entry point(s).
   Put no secrets, tokens, passwords, connection strings, or live URLs that carry
   credentials into `overview`, `explanation`, or `decisions`.
4. **Fill explanations in batches of about 10 nodes.** For each batch: call
   `explain_workflow` per node (pass `includeActivityTree` when the outline helps),
   draft the explanation of logic, arguments, and decisions, then apply one
   `manage_project_file` with `action: "edit"`, `relativePath:
   ".canvas/snapshot.json"`, that replaces only those nodes' empty
   `explanation` / `decisions` fields (and `project.overview` if still empty).
   Do not use `action: "write"` to replace the whole snapshot after the skeleton
   exists: redacted `***REDACTED***` expressions in the file make a full write
   fail under `ProjectFilePolicy`.
5. **`decisions` extraction.** From `explain_workflow` XAML `Activities`, collect
   `DisplayName` for every activity whose `Type` is `If`, `FlowDecision`,
   `Switch`, or `Pick` (parser local names; "Flow Decision" in Studio is
   `FlowDecision`). Write that list in activity-list order. Use `[]` when none
   match. Coded nodes keep `decisions: []`.
6. **Resume from empty explanations.** On a later session, open the snapshot,
   find nodes whose `explanation` is still `""`, and continue in batches of about
   10. Do not regenerate the skeleton unless the developer asks to refresh facts
   after workflow file changes.
7. **Commit the snapshot** with the UiPath project when explanations are good
   enough for a teammate to open the canvas offline.

## 7. Canvas app

Own project, not a page served by this MCP. Chosen stack:

- Vite + React
- React Flow (xyflow) with elkjs for layered layout
- zod for the schema (refusal reasons come from parse failures)
- Vitest for loader fixtures
- Playwright for UI checks
- `vite-plugin-singlefile` so one HTML file opens from `file://` or a
  Teams/SharePoint share

Publish the JSON Schema next to the generation guide in the canvas repo.

### Load and derived graph

After zod accepts the file:

- **Orphans (UI):** every node with no incident edge, plus every edge endpoint
  (`sourceWorkflow` / `targetWorkflow`) that is not a node `id`. Missing
  endpoints render as synthetic nodes marked missing (not present in
  `nodes[]`).
- **Cycles (UI):** Tarjan strongly connected components on the directed graph of
  edges with `isResolved: true` whose endpoints are both known node ids; every
  node that belongs to a component of size > 1, or that has a self-loop, is
  marked as a cycle member.
- **Stale mark:** when the opened snapshot path is
  `<project>/.canvas/snapshot.json` and the workflow file at
  `<project>/<node.id>` is readable, compute SHA-256 of that file and compare to
  `nodes[].sha256`. Mismatch → stale mark on that node. When the app has only
  the JSON (picker chose a copy with no project tree), do not show stale or
  fresh — omit the freshness indicator.

### Screen (one screen after load)

- **Header:** `project.name`, entry-point file label (`project.entryPoint` when
  non-null, otherwise "No main entry point"), and snapshot time from
  `generatedAt`.
- **Overview:** `project.overview` visible above the map before any node is
  selected. Empty overview shows a short "overview not written" note.
- **Map:** pan-and-zoom graph. Arrows run caller → callee
  (`sourceWorkflow` → `targetWorkflow`).
  - When `project.entryPoint` is non-null and equals some `nodes[].id`, that
    node is marked as the entry; layout roots from that node downward.
  - When `project.entryPoint` is null, or non-null but matches no node, no node
    is marked as the entry; layout starts from nodes with no callers among known
    nodes.
  - Every id in `project.additionalEntryPoints` that matches a node is badged
    as an additional entry point on the map and in the node panel. Those badges
    do not replace the main entry mark.
  - The unconnected group holds orphans (no incident edge) and synthetic missing
    endpoints. Coded workflows with no edges fall into this group.
  - Cycle members are marked.
  - Edges with `isResolved: false` are dashed.
  - A file-name filter dims non-matching nodes.
- **Side panel (node):** `kind`, `explanation`, `arguments`, `decisions`,
  `hasExceptionHandler`, and stale mark when applicable. Callers and callees are
  links that select that node and bring it into view. Empty `explanation` leaves
  facts visible and states that the explanation was not written. Non-null
  `parseError` is shown on that node.
- **Side panel (edge):** `displayName` and `argumentMappings` (direction,
  targetArgument, expression).
- Opening another snapshot replaces the screen. The app does not edit the file.

## 8. Failures

### Refuse (open screen names the reason)

- The file is not JSON.
- `schemaVersion` is missing or is not `1`.
- `project`, `nodes`, or `edges` is missing.
- Two nodes share an `id`.

### Still draw (trustworthy file with gaps)

- Empty `explanation`: panel says the explanation was not written.
- Edge endpoint that is not a node `id`: synthetic node marked missing.
- `parseError` set: that message is shown on the node.

## 9. Testing (canvas repo)

Fixture tests on the loader, plus UI checks on a small snapshot. No live UiPath
project and no Copilot.

**Valid fixture** includes: entry point, two invokes, one orphan (no incident
edges), one cycle, one dashed unresolved edge, one unconnected coded workflow,
one empty explanation, one parse error. A second fixture has
`project.entryPoint: null` and at least one `additionalEntryPoints` id.

**Refusal fixtures:** invalid JSON, wrong schema version, duplicate node id,
missing `nodes`.

**On-screen checks:** overview visible on load, entry node marked when
`entryPoint` is set, node panel contents, edge mappings, unconnected group,
cycle mark, filter, caller/callee selection.

**MCP tests (this repo, when implementing the format):** skeleton writes
redacted expressions, empty explanations/decisions, correct node set (XAML +
coded `workflow` only), `sha256` present, `entryPoint` null for library-style
`main`-less projects, `additionalEntryPoints` populated from `EntryPoints`.

## 10. Acceptance criteria

- `generate_documentation(format: "canvasSnapshot")` writes
  `.canvas/snapshot.json` with `schemaVersion` 1, `generatedAt`,
  `generator.mcpVersion`, fact fields filled, explanations and decisions empty.
- Argument-mapping expressions in the file are `SecretRedactor`-processed.
- Copilot, following the generation guide, can fill ~10 nodes per batch and
  resume from empty explanations without rewriting facts.
- The canvas app opens a committed snapshot with no MCP and no Copilot.
- Orphans and cycles are computed in the app, not read from the file.
- A coded-first project shows coded workflow nodes mostly in the unconnected
  group (known limitation).
- Loader refusals and gap rendering match sections 8–9.

## 11. Out of scope

**Canvas app**

- Activity-level or logic-block navigation.
- Editing workflows, regenerating from the canvas, or calling Copilot while
  browsing.
- Coded source files and test files on the map.
- Inventing coded-to-coded invoke edges.

**This MCP (beyond the one format addition)**

- No new tools other than the `canvasSnapshot` format on
  `generate_documentation`.
- No canvas UI hosted by the server.
