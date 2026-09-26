---
name: rpa-authoring
description: "Creates, edits, and debugs UiPath RPA coded (.cs) and XAML (.xaml) workflows with the Engineering MCP. Use when the user asks to create, add, edit, fix, or debug a workflow, coded workflow, test case, activity, or selector. Triggers include 'build a workflow', 'add a coded workflow', 'fix this XAML', 'automate Excel/email/web', 'add a try-catch'. Do not use for multi-step plan implementation, ADRs, AGENTS.md, or documentation. Decline Maestro, IXP, Insights, Agents, and Orchestrator publish."
---

# RPA authoring

Create, edit, and debug `.cs` / `.xaml` in a UiPath project through the Engineering MCP tools on the Copilot connector. This MCP is **RPA only**. Decline Maestro, IXP, Insights, Agents, Coded Apps, Connector Builder, Admin, Governance, solution packaging, Orchestrator runtime, and publish/deploy.

New work is **coded** unless the task is REFramework or orchestration XAML. XAML may invoke coded workflows with BCL and framework types (Dictionary, IEnumerable, DataTable, arrays); never types defined in this automation or source-file methods from XAML.

For a multi-step feature through a plan, use the `guided-implementation-loop` skill. For ADRs, knowledge, `AGENTS.md`, or `Docs\*.md`, use the `project-docs` skill.

## Before writing

1. Confirm `projectPath` (folder that contains `project.json`) inside the allowed roots. Never guess paths. If Memory recalls a `projectPath` or plan state, confirm it with `analyze_project` / `get_implementation_plan` before acting.
2. Call `analyze_project` (`detail=summary`). Pass `workflowFile` or `detail=full` only when one workflow's activities are needed.
3. Confirm file truth with `read_workflow_file` or `search_codebase` (`text` / `symbol` / `activity` / `workflow`).
4. No project yet: stop and ask the user to create it in UiPath Studio. Project scaffolding is not on the Copilot connector.
5. Deep policy lives in the MCP `uipath-rpa` playbook. Call `search_knowledge` with `mode=skill`, `query=uipath-rpa`, and `file` set to a reference from the table below. Do not invent activity property surfaces; look them up with `search_knowledge` (`mode=activity_docs`, `projectPath`, `query`).

## Coded path (default)

1. `add_coded_workflow` with `className` and `kind` `workflow` / `test` / `source`. Process `kind=test` defaults to `Tests\`; pass `relativeFolder` for other layouts (empty string forces the project root). `kind=test` registers `fileInfoCollection`, never `entryPoints`. `kind=workflow` registers `entryPoints`. `kind=source` is a plain helper class.
2. `read_workflow_file`, then surgical edits with `edit_workflow_file`.
3. For a symbol: `navigate_code` with `mode=symbol` (definition), `mode=context` (signature, calls, source by symbol or `file` + `line`), or `mode=references` (usages). Prefer it over reading whole `.cs` files.
4. Coded `[Workflow]` entry: try/catch plus `Log(...)`. Do not put business logic or UI clicks in XAML.
5. Package or `project.json` changes beyond what `add_coded_workflow` registers are not on the Copilot connector: stop and ask the user. Never change `expressionLanguage` or `targetFramework`.

Read the `references/coded/operations-guide.md` playbook file before non-trivial coded work; add `references/coded/codedworkflow-reference.md` (coded rules and the `CodedWorkflow` base class) when fixing compile errors.

## XAML shell

REFramework and `InvokeWorkflowFile` wiring only:

1. New file: `build_workflow` from a minimal spec (for example `{ "name": "Sequence" }`) at the target `relativePath`.
2. Locate the container: `find_activity` (IDs are per-parse-snapshot — recapture after every structural edit; pass `workflowFile` with `activityId`).
3. Insert: `insert_activities` with `activityId`.
4. Arguments and variables: `manage_workflow_data`.

## Spec-based XAML

When the shell pair is not enough (full activity surface):

1. Read `references/activity-spec.md`, bundled with this skill. It is the spec grammar: node shape, container slots, and the expression-language rules (VisualBasic uses `[expr]` brackets; CSharp takes raw expressions with no brackets).
2. Unknown activity type or properties: `search_knowledge` with `mode=activity_docs`.
3. Dry-run: `validate_activity_spec` with `projectPath`. Do not write files from an invalid spec.
4. New file: `build_workflow`. Existing file: `insert_activities`.
5. Arguments and variables: `manage_workflow_data`.

Read the `references/xaml/xaml-basics-and-rules.md` playbook file before generating XAML; add `references/xaml/common-pitfalls.md` when a validation error persists.

## UI automation (placeholders)

Live selector indication and Object Repository capture are out of Copilot scope.

When the work is UI (open an app/browser, click, type, scrape, submit, verify UI state), emit **real** UIA activities (`NApplicationCard`, `NTypeInto`, `NClick`, `NGetText`, or coded `uiAutomation.Open` / `Attach` / `TypeInto` / `Click`) with **placeholder selectors** and a **`TODO Indicate`** marker — `DisplayName` on XAML, `// TODO[Indicate]` next to the coded call. A developer opens Studio, clicks Indicate on each marked activity, and the workflow runs.

Do not replace UIA steps with `Log` stubs. Do not substitute Selenium, Playwright, Chrome DevTools Protocol, raw DOM JavaScript, or HTTP form posts for UI interaction.

Read the `references/uia-starter-guide.md` playbook file and follow its Placeholder-Selector Stub Pattern section before any UIA work.

## Deep references

Read each with `search_knowledge` (`mode=skill`, `query=uipath-rpa`, `file=<path>`):

| Need | `file` |
|------|--------|
| Coded vs XAML / hybrid | `references/coded-vs-xaml-guide.md` |
| Coded create/edit | `references/coded/operations-guide.md` |
| Coded rules / `CodedWorkflow` API | `references/coded/codedworkflow-reference.md` |
| XAML anatomy and authoring flow | `references/xaml/xaml-basics-and-rules.md` |
| XAML validation pitfalls | `references/xaml/common-pitfalls.md` |
| UI automation / placeholders | `references/uia-starter-guide.md` |
| Try/Catch, retry, GEH | `references/error-handling-guide.md` |
| Test cases | `references/testing-guide.md` |
| REFramework | `references/reframework-guide.md` |

## After every change

1. Confirm writes with `read_workflow_file` or `search_codebase`. Never rewrite a redacted credential body (`***REDACTED***`) back to disk.
2. `check_work` with `projectPath` and `files` set to the project-relative files you touched. One call returns the Roslyn compile, `validate`, and scoped gaps. Remediate resilience, observability, structure, and coded/XAML boundary issues it reports.
3. If a plan task is in progress, `update_plan_task` to `done` or `blocked`. Marking done is not blocked on ADR or knowledge freshness.

Use `checkpoint` before a risky edit, and `get_changes` / `revert_changes` to inspect or undo it. Start `validate_project` with `build: true` (then poll `get_job`) only when the user asks for an authoritative CLI compile.

On structured errors, read `fixHint`, correct the call, and retry.

Understand an existing file with `explain_workflow` or `get_workflow_dependencies` — those calls do not write.

## Stop

Stop and ask when the project path is ambiguous, a wholesale file rewrite seems necessary, live Indicate is required, or `check_work` stays red after retries. Surgical `.cs` edits go through `edit_workflow_file`; XAML inserts go through `insert_activities`.
