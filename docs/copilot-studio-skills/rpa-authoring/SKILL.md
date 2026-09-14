---
name: rpa-authoring
description: "Creates, edits, and debugs UiPath RPA coded (.cs) and XAML (.xaml) workflows with the Engineering MCP. Use when the user asks to create, add, edit, fix, or debug a workflow, coded workflow, test case, activity, or selector. Triggers include 'build a workflow', 'add a coded workflow', 'fix this XAML', 'automate Excel/email/web', 'add a try-catch'. Do not use for multi-step plan implementation, ADRs, AGENTS.md, or documentation. Decline Maestro, IXP, Insights, Agents, and Orchestrator publish."
---

# RPA authoring

Create, edit, and debug `.cs` / `.xaml` in a UiPath project through advertised Engineering MCP tools. This MCP is **RPA only**. Decline Maestro, IXP, Insights, Agents, Coded Apps, Connector Builder, Admin, Governance, solution packaging, Orchestrator runtime, and publish/deploy.

New work is **coded** unless the task is REFramework or orchestration XAML. XAML may invoke coded workflows with BCL and framework types (Dictionary, IEnumerable, DataTable, arrays); never types defined in this automation or source-file methods from XAML.

For a multi-step feature through a plan, use the `guided-implementation-loop` skill. For ADRs, knowledge, `AGENTS.md`, or `Docs\*.md`, use the `project-docs` skill.

## Before writing

1. Confirm `projectPath` (folder that contains `project.json`) inside the allowed roots. Never guess paths.
2. Call `analyze_project` (`detail=summary`). Pass `workflowFile` or `detail=full` only when one workflow's activities are needed.
3. Confirm file truth with `read_workflow_file` or `search_codebase` (`text` / `symbol` / `activity` / `workflow`).
4. No project yet and the user asked to create one: `create_project`, then `analyze_project`.
5. Deep policy lives in the MCP `uipath-rpa` playbook. Call `read_skill` with `name` `uipath-rpa` and `file` set to the reference below. Do not invent activity property surfaces.

## Coded path (default)

1. `add_coded_workflow` with `kind` `workflow` / `test` / `source`. Process `kind=test` defaults to `Tests\`; pass `relativeFolder` for other layouts (empty string forces the project root). `kind=test` registers `fileInfoCollection`, never `entryPoints`. `kind=workflow` registers `entryPoints`. `kind=source` is a plain helper class.
2. `read_workflow_file`, then surgical edits with `edit_workflow_file`.
3. Fast `.cs` check: `get_compile_errors`. For a symbol: `find_code_symbol` → `get_code_context` → `find_code_references`.
4. Coded `[Workflow]` entry: try/catch plus `Log(...)`. Do not put business logic or UI clicks in XAML.
5. Dependencies, entry points, or `fileInfoCollection`: `patch_project_json`. Never change `expressionLanguage` or `targetFramework`.

Call `read_skill("uipath-rpa", file: "references/coded/operations-guide.md")` before non-trivial coded work; add `references/coded/coding-guidelines.md` when debugging compile errors.

## XAML shell

REFramework and `InvokeWorkflowFile` wiring only:

1. New blank file: `add_xaml_workflow`.
2. Locate the container: `find_activity` (IDs are per-parse-snapshot — recapture after every structural edit).
3. Insert: `insert_activities`.
4. Arguments and variables: `manage_workflow_data`.

## Spec-based XAML

When the shell pair is not enough (full activity surface):

1. Unknown activity type: `recommend_activities`.
2. Dry-run: `validate_activity_spec`. Do not write files from an invalid spec.
3. New file: `build_workflow`. Existing file: `insert_activities`.
4. Arguments and variables: `manage_workflow_data`.

Spec shape: `{ name, properties, children, variables (root only), catches (TryCatch only), else (If), cases/default (Switch), arguments (InvokeWorkflowFile) }`. Strings in `[expr]` brackets are expressions; everything else is a literal. `If` `children` is the Then branch; `else` is the Else branch.

Call `read_skill("uipath-rpa", file: "references/xaml/xaml-basics-and-rules.md")` before generating XAML; add `references/xaml/workflow-guide.md` for the authoring phases.

## UI automation (placeholders)

Live selector indication and Object Repository capture are out of Copilot scope.

When the work is UI (open an app/browser, click, type, scrape, submit, verify UI state), emit **real** UIA activities (`NApplicationCard`, `NTypeInto`, `NClick`, `NGetText`, or coded `uiAutomation.Open` / `Attach` / `TypeInto` / `Click`) with **placeholder selectors** and a **`TODO Indicate`** marker — `DisplayName` on XAML, `// TODO[Indicate]` next to the coded call. A developer opens Studio, clicks Indicate on each marked activity, and the workflow runs.

Do not replace UIA steps with `Log` stubs. Do not substitute Selenium, Playwright, Chrome DevTools Protocol, raw DOM JavaScript, or HTTP form posts for UI interaction.

Call `read_skill("uipath-rpa", file: "references/ui-automation-guide.md")` and follow the Placeholder-Selector Stub Pattern before any UIA work.

## Deep references

| Need | `read_skill("uipath-rpa", file: ...)` |
|------|--------------------------------------|
| Coded vs XAML / hybrid | `references/coded-vs-xaml-guide.md` |
| Coded create/edit | `references/coded/operations-guide.md` |
| XAML create/edit | `references/xaml/workflow-guide.md` |
| XAML anatomy | `references/xaml/xaml-basics-and-rules.md` |
| UI automation / placeholders | `references/ui-automation-guide.md` |
| Try/Catch, retry, GEH | `references/error-handling-guide.md` |
| Test cases | `references/testing-guide.md` |
| New project / templates | `references/environment-setup.md` |

## After every change

1. Confirm writes with `read_workflow_file` or `search_codebase`. Never rewrite a redacted credential body (`***REDACTED***`) back to disk.
2. `validate_project` with `build: false` and `pack: false`. Pass `build: true` only for an authoritative CLI compile.
3. `analyze_project_gaps`. Remediate resilience, observability, structure, and coded/XAML boundary gaps. Ignore `category=docs`.
4. If a plan task is in progress, `update_plan_task` to `done` or `blocked`. Marking done is not blocked on ADR or knowledge freshness.

On structured errors, read `fixHint`, correct the call, and retry.

Understand an existing file with `explain_workflow` or `get_workflow_dependencies` — those calls do not write.

## Stop

Stop and ask when the project path is ambiguous, a wholesale file rewrite seems necessary, live Indicate is required, or validation stays broken after retries. Surgical `.cs` edits go through `edit_workflow_file`; XAML inserts go through `insert_activities`.
