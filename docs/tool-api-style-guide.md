# Tool API style guide

Conventions for `[McpServerTool]` methods in `src/UiPath.Engineering.Mcp.Tools`. Identifiers are camelCase in the C# signature; the SDK serializes them to the protocol unchanged.

## Path parameters

| Name | Meaning | Used for |
|------|---------|----------|
| `projectPath` | Absolute path to a UiPath project directory that contains `project.json`. | The project the call operates on. Every project-scoped tool takes it. |
| `relativePath` | File path relative to the project root, e.g. `'Main.xaml'`, `'Workflows/SendEmail.cs'`, `'docs/notes.md'`. | Reading, editing, and creating files inside the project. |
| `filePath` | Project-relative workflow or coded file the CLI acts on, e.g. `'Main.xaml'`. | `run_workflow`, `control_debug_session`, `patch_project_json`. |
| `workflowFile` | Workflow file name with or without the extension, optionally a path. | Read-only lookups that accept a loose name and resolve it: `find_activity`, `get_workflow_dependencies`, `explain_workflow`. |
| `file` | Project-relative `.cs` path, passed together with `line`. | `navigate_code` (`mode=context`), where the member is located by `symbol` or by `file` + `line`. |
| `workingDirectory` | Absolute directory used as the process working directory. | `run_ui_path_cli` only. |
| `parentDirectory` | Absolute directory that receives a new project folder. | `create_project` only. |

`projectPath` is absolute. Every other file argument is project-relative and is resolved through `PathPolicy.TryResolveProjectRelative`, which refuses paths that escape the project. Parameters that take an absolute path outside the project (`workingDirectory`, `parentDirectory`, `libraryPaths`, `nupkgPath`, `nugetSourcesConfigPath`) are additionally constrained to `Projects:AllowedRoots` and say so in their `[Description]`.

Do not introduce a second name for an existing role. A new tool that edits one file inside a project takes `projectPath` + `relativePath`; one that runs a file through the CLI takes `projectPath` + `filePath`; one that resolves a workflow by name takes `projectPath` + `workflowFile`.

## Closed-set string parameters

Enum-like parameters are lower-case tokens and are validated with `ToolArgs.ParseChoice` (or `ToolArgs.ParseEnum` for a real enum), which returns a structured `InvalidArgument` error listing the accepted values. A parameter that accepts a closed set is never validated by an ad-hoc `switch` default.

Identifier names, by role:

- `operation` — what the tool does, when the tool is a dispatcher over several verbs: `manage_packages`, `patch_project_json`, `manage_workflow_data`, `edit_workflow_activity`.
- `action` — the same role where the tool manages a resource: `manage_project_content`.
- `kind` — the category of the thing being created or managed: `add_coded_workflow`, `manage_project_content`, `manage_workflow_data`, `search_knowledge`.
- `mode` — the search or navigation strategy: `search_codebase`, `navigate_code`, `search_knowledge`.
- `source` — which repository or origin to read: `get_object_repository`.
- `detail` — response verbosity: `analyze_project`.
- `position` — insertion placement, `first` or `last`: `insert_activities`, `edit_workflow_activity`.
- `status` — the target state of a plan task: `update_plan_task`.
- `scope` — the CLI rule scope: `get_analyzer_rules`.

Accepted values are listed inline in the `[Description]` in the same order the code accepts them: `"Operation: install, versions, or inspect."` When a value is the default, say so: `"Which repository to read: project (the project's own entries) or library (...)."`

## Optional parameters and defaults

Required parameters come first and take no default. Optional parameters follow and always carry one: `= null` for an optional string, list, or nullable number; `= false` for a flag. Boolean defaults are conservative — a call that omits the flag never mutates, overwrites, or executes:

- `overwrite = false`, `replaceAll = false`, `build = false`, `pack = false`, `allowUnknownActivities = false`, `skipBuild = false`.

A flag that changes behavior materially states the default in its description and names the reason it is off: `"Allow replacing an existing file at relativePath. When false (default), an existing file is never overwritten."`

## Descriptions

Every parameter carries `[Description]`. Every tool method carries one `[Description]` on the `[McpServerTool]` attribute, plus `Title`, `ReadOnly`, `Destructive`, and `Idempotent` matching real behavior.

- Tool and parameter descriptions stay at most about 400 characters.
- The tool description states what the call does, when to use it, and the next tool (`Next: validate_project.`). Do not paste activity-spec grammar into tool descriptions — that lives at MCP resource `uipath://authoring/activity-spec`.
- Absolute-path parameters name their guard: `"Absolute path to the UiPath project directory (must contain project.json)."` or `"... Must be inside Projects:AllowedRoots."`
- Project-relative file parameters give a concrete example: `"Path of the .xaml file relative to the project root, e.g. 'Main.xaml'."`
- Numeric parameters state their range and default: `"Workflows per page when detail=full (1-50, default 20)."`, `"Optional CLI timeout in seconds (default 300, max 3600)."`
- A parameter whose meaning depends on another parameter says which one: `"For command=start: workflow or coded file to debug, relative to the project root, e.g. 'Main.xaml'."`

## Collections

Repeatable CLI-shaped values are `List<string>?` with the element format stated in the description, e.g. `"'name=John'", "'retries:=3'", "'payload=@file.json'"`. Structured input is a typed list (`List<PlanTaskInput>`, `List<WorkItemInput>`) so the protocol schema exposes the fields.

## Results

Tools return `ToolResult` (serialized camelCase): `status`, `summary`, `data`, `errors`, `errorDetails`, `warnings`, `durationMs`. Failures set `status = "error"` and populate `errorDetails` with `ToolError(errorCode, message, fixHint)`; a failure never throws out of the tool. Tool-specific payloads go in `data` as an anonymous object with camelCase members.

`summary` is one sentence of outcome, prefixed with the count or target the call acted on: `"Found 12 text match(es) for 'queue' across 4 file(s)."`, `"Spec-based activities inserted into 'sequence.1/if.1' in 'Main.xaml'."` When a call has an evident next step in the authoring loop, the summary names it as `Next: <tool_name>` — the same pointer the tool description carries.