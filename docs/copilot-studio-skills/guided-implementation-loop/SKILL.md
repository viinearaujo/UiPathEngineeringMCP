---
name: guided-implementation-loop
description: "Implements a UiPath RPA feature (.xaml/.cs) through a governed plan → implement → verify loop on the Engineering MCP. Use when the user asks to build, add, or implement a feature, work through the plan, or fix gaps across multiple tasks. Triggers include 'implement this feature', 'work the plan', 'fix the gaps', 'build the automation'. Do not use for single one-line edits, pure analysis, or documentation generation. Decline Maestro, IXP, Insights, and Agents."
---

# Guided implementation loop

Turns a feature request into a governed loop over advertised Engineering MCP tools. The server is passive — drive the sequence, one deterministic tool call at a time: plan first, dry-run before writing, verify after every task, stop when told.

This MCP is **RPA only** (`.xaml` / `.cs`). Decline Maestro, IXP, Insights, Agents, Orchestrator runtime, and publishing.

Single-file create/edit/debug without a multi-step plan belongs to `rpa-authoring`. ADRs, knowledge, `AGENTS.md`, and `Docs\*.md` belong to `project-docs`.

## Phase 0 — Scope check

1. Confirm `projectPath` (folder that contains `project.json`) inside the allowed roots. Never guess paths.
2. Call `analyze_project` (`detail=summary`) for structure, workflow names, and file paths.
3. Restate the goal in one sentence and confirm it if the request is vague. Do not start a plan for a requirement that cannot map to concrete workflows or activities.

## Phase 1 — Plan

1. Call `get_implementation_plan` first.
2. If no plan exists, call `create_implementation_plan` with the goal and an ordered task list. Each task must be small enough to verify independently (one workflow, one edit, one data change).
3. If a plan already exists, resume it. Do not call `create_implementation_plan` unless none exists. Never pass `overwrite: true` unless the user explicitly asked to replace the plan.
4. Present the task list before implementing. Proceed on approval, or on the user's original instruction if they already said "implement it".

## Phase 2 — Implement, one task at a time

For each task, in order:

1. Call `update_plan_task` → `in_progress`.
2. New work is **coded** unless the task is REFramework or orchestration XAML.
   XAML may invoke coded workflows with BCL and framework types (including Dictionary, IEnumerable, DataTable, and arrays); never types defined in this automation or source-file methods from XAML.
   - Coded: `add_coded_workflow` (`kind` `workflow` / `test` / `source`); Process `kind=test` defaults to `Tests\`; pass `relativeFolder` for other layouts (empty string forces the project root). Edit `.cs` with `edit_workflow_file` after `read_workflow_file`. `kind=test` registers `fileInfoCollection`, never `entryPoints`. Fast `.cs` check: `get_compile_errors`.
   - XAML shell: `find_activity` then `insert_activities` for REFramework and `InvokeWorkflowFile` only. New blank XAML: `add_xaml_workflow`.
   - Spec-based XAML (full surface): `recommend_activities` when the activity type is unknown; dry-run `validate_activity_spec` before `build_workflow` / `insert_activities`. Do not write files from an invalid spec. Variables/arguments: `manage_workflow_data`.
3. Spec shape: `{ name, properties, children, variables (root only), catches (TryCatch only), else (If), cases/default (Switch), arguments (InvokeWorkflowFile) }`. Strings in `[expr]` brackets are expressions; everything else is a literal. `If` `children` is the Then branch; `else` is the Else branch.
4. UI steps without live capture: real UIA activities with placeholder selectors and `TODO Indicate` markers. Call `read_skill("uipath-rpa", file: "references/ui-automation-guide.md")` for the Placeholder-Selector Stub Pattern. Do not emit `Log` stubs.
5. If a tool returns a structured error, read the `fixHint`, correct the call, and retry. After repeated failures on the same task, mark it `blocked` with notes and ask the user instead of guessing.

Deep coded/XAML policy: `read_skill("uipath-rpa", file: "references/coded/operations-guide.md")` or `references/xaml/xaml-basics-and-rules.md`.

## Phase 3 — Verify after every task

After **one** task:

1. Confirm the files you wrote with `read_workflow_file` or `search_codebase`. Never rewrite a redacted credential body (`***REDACTED***`) back to disk.
2. Call `validate_project` with `build: false` and `pack: false`. Pass `build: true` only for an authoritative CLI compile.
3. Call `analyze_project_gaps`. Remediate resilience, observability, structure, and coded/XAML boundary gaps. Ignore `category=docs` — docs freshness does not block RPA done.
4. Call `update_plan_task` → `done` when validation succeeded and the files exist.
   The plan at `docs/implementation-plan.json` is a scratchpad. Marking done is not blocked on ADR or knowledge freshness.
   On failure, `update_plan_task` → `blocked` with the validation errors in notes.

Incidental docs (do not block done): file-count or dependency changes → `sync_project_context`; a recorded decision → `manage_project_docs` action `write` kind `adr`; a convention or pitfall → `manage_project_docs` action `write` kind `memory`.

## Phase 4 — Close out

When all tasks are `done` (or the remaining ones are explicitly `blocked`):

1. Call `validate_project` with `build: false` and `pack: false` for a final project check.
2. Report: what was implemented per task, final validation status, and any blocked tasks with their notes. Suggest the next step (`generate_documentation` as a read-only dump, or committing).

## Stop rules

- Stop and ask when: the project path is ambiguous; an existing plan would be overwritten; a wholesale file rewrite seems necessary; a task fails verification repeatedly; or the user interrupts.
- Surgical `.cs` edits go through `edit_workflow_file`; XAML inserts go through `insert_activities`.
- The loop ends when the user's request ends. Do not continue implementing beyond the agreed plan without asking.
