# Design: Copilot-native Skill + RPA CLI Adapter

Date: 2026-08-19
Status: Draft for user review (no implementation until approved)

## 1. Goal

Make Microsoft 365 Copilot a reliable UiPath RPA development client of this MCP
server. Copilot should follow UiPath skill policy the same way Cursor does, then
execute through typed MCP tools — not by inventing `uip rpa …` command strings or
swallowing a 64 KB `SKILL.md`.

Success looks like this, in one Copilot turn:

1. User: "Add a TryCatch around the Excel read in InvoiceProcess."
2. Copilot calls `route_uipath_task` and gets `uipath-rpa`, the XAML error-handling
   playbook slice, and the preferred MCP tools.
3. Copilot uses existing authoring tools (`find_activity`, `validate_activity_spec`,
   `insert_activities`, …) and typed RPA operations (`rpa_exec`) for CLI steps the
   skill would have run in a shell.
4. Copilot never dumps the full `uipath-rpa` skill, never calls `run_ui_path_cli`
   for a command that already has a typed tool, and finishes with `verify_work`.

Non-goals for this spec:

- Wrapping every UiPath product CLI (`admin`, `ixp`, `maestro`, `gov`, …) as typed
  operations. Those skills stay readable; execution stays on the existing
  `run_ui_path_cli` escape hatch (and its verb allowlist).
- Rewriting files under `.agents/skills/` (they ship from `@uipath/skills`).
- Running a skill loop inside the server. The server stays passive and
  deterministic: one tool call in, structured JSON out.
- Removing or renaming the current 33 tools (Copilot Studio registrations would
  break). Additive only.

## 2. Locked assumptions

These come from the repo and the request. Change them in review if they are wrong.

| Assumption | Why it is locked |
|---|---|
| Primary client is Microsoft 365 Copilot over Streamable HTTP (`/sse`). | README + implementation handoff. Cursor/Inspector remain supported, not the design center. |
| Copilot cannot auto-load Cursor skills. Skills reach Copilot only through MCP tools. | Copilot has no `.agents/skills` reader. |
| Copilot may ignore MCP prompts, resources, and `initialize.instructions`. | Routing must work as **tools + tool results**, with instructions as a bonus for clients that honor them. |
| Tool-count pressure is real. Adding 15 `rpa_*` tools would make selection worse. | Copilot Studio imports the full tool list; 33 is already large. |
| `uipath-rpa` is the RPA development playbook. MCP authoring tools are the execution surface. | Skills say `uip rpa validate`; this server already has `validate_project`. |
| Existing non-negotiables stand: C# / .NET 8, structured JSON, path allowlisting, no arbitrary shell, secrets redacted, no chatbot in the server. | `documentation/UiPathEngineeringMCP-ImplementationPlan.md` §4. |

## 3. Current-state diagnosis

### 3.1 Skills wrapping is a dump, not a navigator

`list_skills` returns `{ name, description, directory }`. `read_skill` returns the
whole file (capped at `Skills:MaxSkillFileBytes` = 64 KiB).

That matches Cursor's "open `SKILL.md`" habit and fails Copilot:

- `uipath-rpa/SKILL.md` is a **router**. It tells the agent which reference to open
  next (`references/xaml/workflow-guide.md`, `validation-guide.md`, …). Dumping the
  router does not dump the procedure.
- The skill tree is huge (hundreds of files under `uipath-rpa/references/`). Copilot
  has no `list_skill_files`, no heading outline, no section read, no search.
- Frontmatter `when_to_use` is ignored. Catalog search lives in
  `uipath-skill-catalog` and `uip skills search`, neither of which Copilot can run.

### 3.2 CLI wrapping is stringly-typed

`run_ui_path_cli(verb, arguments)` is a quoted command line behind an allowlist.
Copilot must assemble `validate --project-dir "…" --output json` from skill text.

Consequences:

- Mutating subcommands are blocked unless `UiPathCli:EnableMutatingCommands` is
  true, so Copilot hits a wall on `init` / `packages install` / `run` unless those
  exist as first-class tools.
- Commands the server already wraps (`uip rpa validate` / `build` / `pack` /
  `init`) compete with the generic runner. Two ways to do the same thing.
- Per-file validate (`--file-path`), `run`, `templates search`, `packages install`,
  `packages versions`, and `activities find` have no typed tool today.

### 3.3 Dual instruction sources

Cursor loads `uipath-rpa` and runs `uip` in a shell. Copilot sees 33 MCP tools
whose descriptions never mention the skills, plus `list_skills` whose description
says "load the full instructions before doing UiPath work."

Copilot therefore either skips the playbook and guesses at tools, or loads
`SKILL.md` and tries to run CLI strings through `run_ui_path_cli`. Both are
worse than a binding that says: *skill policy here, MCP tool there*.

### 3.4 Copilot-specific friction

- All 33 tools are advertised on every `tools/list`. No packs, no "RPA development"
  subset, no server instructions.
- `ToolResult` has `SuggestedTool` only on some errors. Success responses do not
  tell Copilot what to call next.
- `docs/skills/guided-implementation-loop/SKILL.md` is a Copilot playbook that
  lives in this repo, not in the `@uipath/skills` catalog, and is not surfaced by
  `list_skills` unless it sits under `Skills:SkillsRoot`.

## 4. Approaches

### A — Thin retrieval upgrade

Enhance `list_skills` / `read_skill` (query, file listing, heading outline, section
slices) and add `list_rpa_commands` that prints allowlisted `uip rpa` help.

- Pros: small diff; existing tools keep working.
- Cons: Copilot still stitches playbook text to CLI strings; still two ways to
  validate; tool-count and dual-source problems stay.

### B — Copilot-native Skill + RPA adapter (recommended)

Keep skills as the policy source of truth. Add a **router tool** that returns a
playbook slice plus a **CLI→MCP binding map**. Execute RPA development CLI through
typed operations (`rpa_exec`) for gaps, and through existing tools where they
already exist. Put Copilot routing in tool results first, server instructions
second.

- Pros: neat seam (skills stay untouched; MCP translates); Copilot gets a single
  entry point; tool count grows by two; matches "server stays passive."
- Cons: MCP must maintain a binding file as `uip rpa` evolves; Copilot Studio
  still sees ~35 tools unless an enable-subset is documented.

### C — Skill execution engine

Parse skill markdown into a graph and run the loop inside the server
("execute uipath-rpa for this goal").

- Rejected. It builds a chatbot in the MCP server, couples the server to skill
  markdown structure, and fights every future `@uipath/skills` release.

**Recommendation: B.** A is a subset of B's skill-navigator work and should not
ship alone. C violates the existing architecture rule.

## 5. Architecture

Three planes. Copilot talks only to tools; the planes sit behind them.

```text
Microsoft 365 Copilot
        │  Streamable HTTP /sse
        ▼
┌─────────────────────────────────────────────┐
│  Tool surface (additive)                    │
│  route_uipath_task                          │
│  list_skills / read_skill  (enhanced)       │
│  rpa_exec                  (new)            │
│  existing 33 tools         (unchanged names)│
│  run_ui_path_cli           (escape hatch)   │
└─────────────┬───────────────────────────────┘
              │
    ┌─────────┴──────────┐
    ▼                    ▼
┌──────────────┐  ┌─────────────────────┐
│ Skill plane  │  │ RPA execution plane │
│ catalog      │  │ existing CLI tools  │
│ outline      │  │ rpa_exec operations │
│ sliced read  │  │ policy (allowlist)  │
│ binding map  │  │ structured parse    │
└──────────────┘  └─────────────────────┘
```

Contract Copilot is taught (via `route_uipath_task` output and optional server
instructions):

1. Call `route_uipath_task` first for any UiPath development request.
2. Follow `preferredTools` and `nextReads`. Do not open a whole `SKILL.md`.
3. When a playbook mentions `uip rpa <subcommand>`, use the bound MCP tool, not
   `run_ui_path_cli`.
4. Use `run_ui_path_cli` only when the binding map says `escapeHatch: true`.

## 6. Skill plane

### 6.1 Catalog enrichment

Extend `SkillSummary` with fields already present in skill frontmatter / the
generated catalog, without parsing full bodies at list time:

- `name`, `description`, `directory` (existing)
- `whenToUse` (frontmatter `when_to_use`, or `description` if that key is absent)

`list_skills` gains an optional `query`. When set, rank by case-insensitive
substring match against `name`, `description`, and `whenToUse`. Unfiltered list
remains the default.

Also read `.agents/skills/skills.catalog.json` when present (schemaVersion 1,
`skills[].name/description/skillPath`) as a faster catalog than scanning every
`SKILL.md`. Fall back to today's directory scan if the JSON is missing. The
catalog is generated by `uip skills catalog` and is safe to overwrite; the MCP
must not require it.

### 6.2 Outline and sliced read

Add to `ISkillsProvider` (no third Copilot tool — these feed `read_skill`):

- `ListFilesAsync(name, includeActivityDocs = false)` — skill-relative paths,
  `SKILL.md` first, then `references/**/*.md`. Skip `activity-docs/` unless
  opted in (hundreds of generated pages).
- `GetOutlineAsync(name, file?)` — markdown ATX headings (`#` … `######`) with
  line numbers and anchor slugs. Default file is `SKILL.md`.
- `ReadAsync` gains `section` (heading text or slug) and `startLine`/`lineCount`
  pagination. If `section` is set, return that heading through the next heading
  of the same or higher level. Existing `file` parameter stays.

`read_skill` arguments after this change: `name`, `file?`, `section?`,
`startLine?`, `lineCount?`, `outlineOnly?` (bool, default false),
`includeActivityDocs?` (bool, default false).

Behavior:

- `read_skill(name)` with no `file` still targets `SKILL.md`.
- `outlineOnly: true` → `{ outline, files }`, no body.
- `section` set → that heading through the next same-or-higher heading. If the
  slice itself exceeds 12 KiB, truncate the slice and set `truncated: true`.
- No `section`, file ≤ 12 KiB → full body (today's behavior for small files).
- No `section`, file > 12 KiB → `{ outline, files, content: first 12 KiB,
  truncated: true }` plus `nextActions` for the next `file`/`section`. This is
  an intentional behavior change: oversized dumps are no longer the happy path.
- `MaxSkillFileBytes` remains the hard cap for any single body payload.

### 6.3 `route_uipath_task` (new tool)

Arguments:

| Argument | Required | Meaning |
|---|---|---|
| `goal` | yes | Natural-language user request |
| `projectPath` | no | When set and allowed, peek `project.json` + file kinds to pick XAML vs coded vs hybrid |

Deterministic routing, no LLM in the server:

1. Score installed skills: substring / token overlap of `goal` against
   `name` + `description` + `whenToUse`.
2. Tie-break with this static specificity order (first match wins):
   `uipath-rpa`, `uipath-maestro-flow`, `uipath-maestro-bpmn`,
   `uipath-maestro-case`, `uipath-agents`, `uipath-solution`,
   `uipath-platform`, `uipath-troubleshoot`, then alphabetical.
   Domain skills beat platform/ops when both mention "workflow".
3. If `projectPath` is present and allowed, detect mode from `project.json`
   `targetFramework` (Legacy / Windows / Portable) and a files-only peek
   (`.xaml` vs `.cs` with `[Workflow]`). Do not build the full project model.
4. Look up the winner in the **binding map** (section 7). Skills without a
   map still route; `cli` is empty and `preferredTools` is
   `[{ tool: "read_skill" }, { tool: "run_ui_path_cli" }]` when the skill's
   CLI verb is in `AllowedVerbs`, otherwise `[{ tool: "read_skill" }]`.
5. Return the JSON object in §8.

If no skill scores above a floor (no token overlap), return
`SKILL_NOT_ROUTED` with `list_skills` as `SuggestedTool` and the top 5 catalog
rows — do not guess `uipath-rpa`.

This tool does not execute CLI and does not write files.

## 7. RPA execution plane

### 7.1 Binding map

A versioned JSON file in this repo, not in `@uipath/skills`:

`src/UiPath.Engineering.Mcp.Providers/Skills/Bindings/rpa-cli-bindings.json`

Skills stay CLI-oriented for Cursor. This file is the Copilot translation.

Shape:

```json
{
  "schemaVersion": 1,
  "skill": "uipath-rpa",
  "intents": [
    {
      "id": "create-project",
      "keywords": ["new project", "create project", "scaffold", "init"],
      "playbookFile": "SKILL.md",
      "playbookSection": "Common Rules",
      "nextReads": ["references/environment-setup.md"],
      "preferredTools": [
        { "tool": "create_project", "when": "scaffold a new project" }
      ]
    },
    {
      "id": "edit-xaml",
      "keywords": ["xaml", "workflow", "trycatch", "activity"],
      "playbookFile": "references/xaml/workflow-guide.md",
      "preferredTools": [
        { "tool": "analyze_project", "when": "before authoring" },
        { "tool": "find_activity", "when": "locate the target activity" },
        { "tool": "validate_activity_spec", "when": "dry-run before write" },
        { "tool": "insert_activities", "when": "spec-based insert" },
        { "tool": "verify_work", "when": "after the edit" }
      ]
    }
  ],
  "cli": [
    {
      "command": "uip rpa init",
      "tool": "create_project",
      "escapeHatch": false
    },
    {
      "command": "uip rpa validate",
      "tool": "validate_project",
      "escapeHatch": false,
      "notes": "Pass workflowFile to rpa_exec validate_file for per-file validate."
    },
    {
      "command": "uip rpa build",
      "tool": "compile_project",
      "escapeHatch": false
    },
    {
      "command": "uip rpa pack",
      "tool": "validate_project",
      "args": { "pack": true },
      "escapeHatch": false
    },
    {
      "command": "uip rpa run",
      "tool": "rpa_exec",
      "operation": "run",
      "escapeHatch": false
    },
    {
      "command": "uip rpa packages install",
      "tool": "rpa_exec",
      "operation": "packages_install",
      "escapeHatch": false
    },
    {
      "command": "uip rpa packages versions",
      "tool": "rpa_exec",
      "operation": "packages_versions",
      "escapeHatch": false
    },
    {
      "command": "uip rpa templates search",
      "tool": "rpa_exec",
      "operation": "templates_search",
      "escapeHatch": false
    },
    {
      "command": "uip rpa activities find",
      "tool": "rpa_exec",
      "operation": "activities_find",
      "escapeHatch": false
    },
    {
      "command": "uip rpa validate --file-path",
      "tool": "rpa_exec",
      "operation": "validate_file",
      "escapeHatch": false
    }
  ]
}
```

Intent matching is keyword overlap against `goal`, same as skill scoring. The
map is the only place MCP tool names appear. Do not write MCP tool names into
skill markdown.

UIA (`uip rpa uia …`) stays out of this map. Those subcommands belong to the
UiPath.UIAutomation.Activities package docs; this server has no live-desktop
session. If `goal` is capture/indicate, `route_uipath_task` still selects
`uipath-rpa` and returns the playbook slice plus a warning:
`uiaCliUnavailableOverMcp: true`. Copilot should author placeholder-selector
stubs via spec tools, not invent UIA CLI.

### 7.2 `rpa_exec` (new tool)

One tool, enum `operation`, typed extra arguments. This is the RPA development
CLI that is **not** already a first-class tool.

| `operation` | CLI equivalent | Mutating? | Notes |
|---|---|---|---|
| `validate_file` | `uip rpa validate --file-path --project-dir --output json` | no | Per-file loop the skill requires after every edit. |
| `run` | `uip rpa run --project-dir --output json` | yes | Smoke test; optional `file`, `skipBuild`. |
| `templates_search` | `uip rpa templates search --query --output json` | no | Before `create_project` when the user names a template. |
| `packages_versions` | `uip rpa packages versions --package-id --project-dir --output json` | no | |
| `packages_install` | `uip rpa packages install --packages --project-dir --output json` | yes | |
| `activities_find` | `uip rpa activities find --query --output json` | no | ClassName lookup the skill requires before writing a new activity tag. |

Not in `rpa_exec` (already first-class, bind to the existing tool):

- `init` → `create_project`
- project `validate` / `build` / `pack` → `validate_project` / `compile_project`

Not in this spec:

- `debug start`, Test Manager connect, Data Fabric entity CLI, analyzer-rules
  list, `files diff`, `focus-activity`, any `uip rpa uia` command.

`rpa_exec` uses the same `CliCommandPolicy` + path allowlist + secret redaction +
output cap as `run_ui_path_cli`. It builds the argument vector itself; Copilot
never supplies a raw command string. Mutating operations honor
`UiPathCli:EnableMutatingCommands` and return `MUTATING_COMMAND_DISABLED` with
the same fix hint as today.

JSON `--output` is always appended. Reuse `UiPathCliOutputParser`.

### 7.3 Escape hatch

`run_ui_path_cli` stays. Its description changes to: use only when
`route_uipath_task` (or the binding map) sets `escapeHatch: true`, or for
non-RPA verbs in `AllowedVerbs` (`solution` today).

When Copilot calls it for a bound command (e.g. `verb=rpa`, arguments start with
`validate`), the tool still runs, but the result includes a warning:
`preferTool: validate_project`. No hard reject — Copilot Studio users may have
pinned the old path.

### 7.4 Existing CLI tools stay as they are

Per-file validate is **only** `rpa_exec` / `validate_file`. Do not add
`workflowFile` to `validate_project` — two APIs for the same CLI flag would
recreate the dual-path problem this spec is removing.

`create_project` is unchanged. Template selection is `rpa_exec` /
`templates_search`, then `create_project`. A `template` argument on
`create_project` is out of scope.

## 8. Data flow

`route_uipath_task` result `Data`:

```json
{
  "skill": "uipath-rpa",
  "reason": "goal matched xaml/workflow keywords",
  "mode": "xaml",
  "playbook": {
    "file": "references/error-handling-guide.md",
    "section": "TryCatch",
    "content": "…sliced markdown…",
    "truncated": false
  },
  "nextReads": [
    { "skill": "uipath-rpa", "file": "references/xaml/workflow-guide.md" }
  ],
  "preferredTools": [
    { "tool": "analyze_project", "when": "before authoring" },
    { "tool": "find_activity", "when": "locate the target activity" }
  ],
  "cliBindings": [
    { "command": "uip rpa validate", "tool": "validate_project", "escapeHatch": false }
  ],
  "uiaCliUnavailableOverMcp": false
}
```

`mode` is `xaml` | `coded` | `hybrid` | `legacy` | `unknown`. `unknown` when
`projectPath` was omitted or not allowed. `playbook.content` is the section
slice from the matched intent's `playbookFile` / `playbookSection`, already
passed through the 12 KiB truncation rule.

Happy path (edit an existing XAML workflow):

```text
Copilot
  route_uipath_task(goal, projectPath)
    → skill=uipath-rpa, mode=xaml
    → playbook slice (error-handling or workflow-guide)
    → preferredTools = [analyze_project, find_activity, …]
    → cli bindings for validate/build
  analyze_project(projectPath)
  find_activity(…)
  validate_activity_spec(spec)
  insert_activities(…)
  rpa_exec(operation=validate_file, projectPath, file)
  verify_work(projectPath, taskIds)
```

Every `ToolResult` gains optional `nextActions`:

```csharp
public sealed record NextAction(string Tool, string Reason, string? Operation = null);
```

Populated by:

- `route_uipath_task` — from the binding's `preferredTools`
- `read_skill` when truncated — next section/file
- `rpa_exec` / `validate_project` on success — `verify_work` or `compile_project`
- structured errors — existing `SuggestedTool` plus `nextActions`

`nextActions` is advisory. The server still never loops.

## 9. Copilot integration specifics

### 9.1 Server instructions (bonus channel)

On MCP initialize, set a short `ServerInstructions` string (~1 KB) that restates
the four-step contract in §5. Clients that ignore it still work because
`route_uipath_task` carries the same contract in its tool description and result.

Do not register MCP prompts or resources in this spec. Copilot Studio's MCP
connector is tool-centric; those channels are out of scope.

### 9.2 Tool descriptions

Rewrite only the descriptions of:

- `list_skills` — mention `query` and "prefer `route_uipath_task` to pick a skill"
- `read_skill` — mention `section`, truncation, "do not request whole SKILL.md
  when an outline is enough"
- `run_ui_path_cli` — escape-hatch wording
- `validate_project` / `create_project` / `compile_project` — "bound from
  `uip rpa validate|init|build`; do not use `run_ui_path_cli` for these"

Keep descriptions under ~400 characters. Copilot truncates long ones.

### 9.3 Recommended Copilot Studio enable-set

Document in README, do not enforce in code. For an "RPA development" connection,
enable:

`route_uipath_task`, `list_skills`, `read_skill`, `analyze_project`,
`explain_workflow`, `find_activity`, `get_workflow_dependencies`,
`validate_activity_spec`, `build_workflow`, `insert_activities`,
`manage_workflow_data`, `edit_workflow_activity`, `add_xaml_workflow`,
`add_coded_workflow`, `read_workflow_file`, `edit_workflow_file`,
`create_project`, `rpa_exec`, `validate_project`, `compile_project`,
`analyze_project_gaps`, `create_implementation_plan`, `update_plan_task`,
`get_implementation_plan`, `verify_work`, `search_codebase`

Leave GitLab, generic `write_workflow_file`, and `run_ui_path_cli` off unless
needed. The full list remains available for Inspector / Cursor.

### 9.4 Guided implementation loop

Add `Skills:ExtraSkillRoots` (string array) so
`docs/skills/guided-implementation-loop` is indexed without moving it under
`.agents/skills`. `route_uipath_task` returns it as a second `nextReads` skill
when `goal` matches implement / add-feature / work-through-the-plan, together
with `uipath-rpa` as the primary skill. Do not copy or junction the folder.

## 10. Error handling

Reuse `ToolError` + `fixHint` + `SuggestedTool`. New codes:

| Code | When | Fix |
|---|---|---|
| `SKILL_NOT_ROUTED` | `route_uipath_task` has no overlap | Call `list_skills` or tighten `goal` |
| `SKILL_SECTION_NOT_FOUND` | `read_skill` section missing | Call `read_skill` with no section to get the outline |
| `RPA_OPERATION_UNKNOWN` | `rpa_exec` operation not in the enum | Use a listed operation or the bound first-class tool |
| `RPA_OPERATION_ARGS` | required extra arg missing | Fill the named argument from `fixHint` |
| `UIA_CLI_NOT_IN_MCP` | goal is live capture/indicate | Author placeholder-selector stubs via spec tools |

Existing `MUTATING_COMMAND_DISABLED`, `CLI_VERB_NOT_ALLOWED`,
`CLI_ARGUMENTS_REJECTED`, path guards, and CLI-not-found stay as they are.
`rpa_exec` must not crash when `uip` is absent; return the same structured
CLI-not-found error as `validate_project`.

## 11. Testing

Follow existing xUnit + hand-written fakes (no Moq).

Skill plane:

- Frontmatter `when_to_use` parsed; missing key falls back to `description`.
- `list_skills(query: "xaml")` ranks `uipath-rpa` above unrelated skills.
- Catalog JSON used when present; directory scan when absent or malformed.
- `GetOutlineAsync` returns headings with line numbers; nested `##` under `#`.
- `read_skill` section slice; unknown section → `SKILL_SECTION_NOT_FOUND`.
- Truncation at 12 KiB sets `truncated` and `nextActions`.
- Path escape on `file` still rejected.
- `activity-docs/` omitted from `ListFilesAsync` unless opted in.
- Extra skill roots: `guided-implementation-loop` appears in the catalog.

Router:

- Goal "fix this xaml try-catch" → `uipath-rpa`, edit-xaml intent, preferred
  tools include `find_activity` and `insert_activities`.
- Goal "create a new portable project" → create-project intent, `create_project`.
- Goal "deploy a solution" does **not** invent an RPA exec operation; it routes
  to `uipath-solution` with `run_ui_path_cli` as escape hatch if `solution` is
  allowed.
- Empty / unrelated goal → `SKILL_NOT_ROUTED`.
- `projectPath` with only `.xaml` sets `mode: xaml`.

RPA exec:

- Each operation builds the expected argument vector (quoted paths, `--output json`).
- Mutating ops blocked when the flag is off.
- Shell metacharacters in `projectPath` / extra args rejected.
- CLI-not-found structured error.
- Bound-command warning when `run_ui_path_cli` is used for `rpa validate`.

No live `uip` on the Linux CI image; CLI tests keep using the fake provider.

## 12. Out of scope (explicit)

- Maestro / IXP / Agents / Admin typed CLIs.
- Live UI Automation capture.
- Publish/deploy to Orchestrator (already a later phase in the README).
- Embedding / vector search over skill files.
- Dynamic tool registration per session.
- MCP prompts and resources.
- A `template` argument on `create_project`.
- Changing transport, auth, or Dev Tunnel setup.
- Mass-rewriting skill markdown so it names MCP tools.

## 13. Rollout

1. Skill navigator: catalog query, outline, sliced `read_skill`, extra skill
   roots, tests.
2. Binding map + `route_uipath_task` + `nextActions` on `ToolResult`.
3. `rpa_exec` with the six operations in §7.2.
4. Description rewrites + README Copilot enable-set + server instructions.
5. `run_ui_path_cli` prefer-tool warning for bound commands.

Each step is separately shippable. Copilot keeps working after step 1; the
loop becomes smooth after steps 2–3.

## 14. Why this is the neat wrap

| Concern | Before | After |
|---|---|---|
| Skills | Dump `SKILL.md` | Route → outline → section; policy stays in `@uipath/skills` |
| RPA CLI | Copilot writes command strings | Binding map + typed `rpa_exec` / existing tools |
| Copilot | 33 undifferentiated tools | One entry tool, advisory `nextActions`, documented enable-set |
| Server | Passive JSON tools | Still passive — no in-process skill loop |
| Evolution | Skill CLI drift vs MCP tools | One JSON binding file to update when `uip rpa` changes |
