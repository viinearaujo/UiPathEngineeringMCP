---
name: uipath-engineering-mcp-file-tools
description: How to read and edit UiPath project source files (.cs, .xaml, .json) through the UiPath Engineering MCP server. Use this whenever you need the raw text of any file inside a UiPath project — coded workflows (.cs), XAML workflows, project.json, Config files — or need to make a targeted edit to one. Do NOT ask the user to paste file contents into chat; the MCP server exposes raw file access.
---

# UiPath Engineering MCP — raw file access for UiPath projects

The UiPath Engineering MCP server **does expose raw file read and targeted-edit tools**. They work on **any text file**, including C# coded workflows — not just `.xaml`. The word "workflow" in the tool names refers to UiPath workflow projects, not to a file-type restriction.

**Never tell the user "the MCP doesn't expose a raw C# reader" or ask them to paste a file's contents into the conversation. Use the tools below.**

## Tools

### `read_workflow_file`

Reads the raw text of **any file** inside a UiPath project, with line numbers.

- Works on `.cs`, `.xaml`, `.json`, and any other UTF-8 text file in the project.
- Arguments:
  - `projectPath` — absolute path to the UiPath project root (the folder containing `project.json`).
  - `relativePath` — path of the file **relative to the project root**, e.g. `ConnectAndListSFTPSources.cs` or `Data/Config.json`.
  - `startLine` (optional, default 1) — first line to return.
  - `lineCount` (optional, default 1000) — number of lines to return. Page through large files with repeated calls.
- Output: line-numbered text (`<line>\t<content>`), truncated at the page size.
- Secrets (passwords, tokens, connection strings) are automatically redacted in the output. Secret files themselves (`.env`, `*.pem`, `*.key`, names containing "credentials") are refused.

### `edit_workflow_file`

Makes a **targeted, non-destructive edit** to a `.cs` or `.xaml` file — exact string replacement. You do **not** need to rewrite the whole file.

- Works on both `.cs` (coded workflows) and `.xaml`.
- Arguments:
  - `projectPath`, `relativePath` — same as above.
  - `oldString` — the exact text to replace (must match the file byte-for-byte, including indentation and line endings as returned by `read_workflow_file`).
  - `newString` — the replacement text.
  - `replaceAll` (optional, default `false`) — set `true` to replace every occurrence.
- Fails safely: errors if `oldString` is not found, or if it matches more than once without `replaceAll: true`. Nothing is written on failure.

## Standard workflow for editing a coded workflow

1. Call `read_workflow_file` with the file's `relativePath` to get its current contents (page with `startLine`/`lineCount` if large).
2. Identify the exact snippet to change from the line-numbered output.
3. Call `edit_workflow_file` with that snippet as `oldString` and the corrected code as `newString`.
4. Call the MCP's `validate_project` tool to confirm the project still compiles.

## Full tool catalog (all 22 tools on this MCP server)

### Inspection / analysis

- `analyze_project` — Parses `project.json` and every `.xaml`/`.cs` workflow; returns structured metadata, dependencies, and risks (cycles, orphan workflows). **Call this first** to discover project structure and file paths.
- `explain_workflow` — Structured breakdown of a single workflow (arguments, variables, activities, handlers, invokes, logs; class/methods for `.cs`).
- `generate_documentation` — Structured documentation data for the whole project: metadata, per-workflow summaries, dependency graph, risks.
- `analyze_project_gaps` — Hygiene gap analysis (missing entry point, orphans, missing exception handling/logging/tests, unresolved invokes); names the tool that fixes each gap.

### Raw file access (any text file, including `.cs`)

- `read_workflow_file` — Reads any project text file with line numbers and pagination (see above).
- `edit_workflow_file` — Exact-string replacement in `.xaml`/`.cs` (see above). Prefer this for small changes.
- `write_workflow_file` — Creates or **fully overwrites** a `.xaml` or `.cs` file with caller-supplied content. Use only for new files or intentional full rewrites; prefer `edit_workflow_file` otherwise.

### XAML workflow authoring

- `add_xaml_workflow` — Adds a blank `.xaml` workflow with correct `x:Class` naming.
- `validate_activity_spec` — Dry-run validates a JSON activity spec against the activity catalog (no files touched). Run before `build_workflow`/`insert_activities`.
- `build_workflow` — Creates a `.xaml` workflow from a JSON activity spec.
- `insert_activities` — Inserts spec-described activities into an existing `.xaml`, under the activity located by `DisplayName`.
- `edit_workflow_activity` — Activity-level XAML edits: insert/replace/remove a single activity by `DisplayName`. (XAML-only — **not** for `.cs`; use `edit_workflow_file` there.)
- `manage_workflow_data` — Add/remove/rename arguments and variables on an existing `.xaml`.

### Coded workflow authoring

- `add_coded_workflow` — Adds a Coded Workflow `.cs` (inherits `CodedWorkflow`, `[Workflow]` entry method, registered in `project.json`) or a plain helper `.cs` file.

### Validation / verification

- `validate_project` — Runs the UiPath CLI (`uip rpa validate` / `build` / `pack`) and returns structured per-step results plus recommendations.
- `verify_work` — Rebuilds the model, runs CLI validation, checks expected files exist, and marks implementation-plan tasks done/blocked.

### Project scaffolding

- `create_project` — Scaffolds a new UiPath project via `uip rpa init` (requires the UiPath CLI on the host).

### Implementation planning

- `create_implementation_plan` — Creates a plan (goal + ordered tasks) in `docs/implementation-plan.json`.
- `get_implementation_plan` — Returns the plan with per-status task counts.
- `update_plan_task` — Updates one task's status (`pending`/`in_progress`/`done`/`blocked`) and notes.

### GitLab integration

- `search_repository` — Searches GitLab issues in the configured project.
- `create_work_items` — Creates GitLab issues from `{ title, description, labels? }` items.

## Notes for the agent

- `projectPath` must be the project root folder, not the solution or a parent directory.
- If `read_workflow_file` does not appear in your current tool list, the conversation may be running with a stale tool snapshot — tell the user to restart the MCP server / open a new chat, and check that the tool is enabled in the chat's tool picker. Do not conclude the tool doesn't exist.
