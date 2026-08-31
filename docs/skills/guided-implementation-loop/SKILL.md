---
name: guided-implementation-loop
description: "Implement a feature in a UiPath RPA project (.xaml/.cs) through a governed plan → implement → verify loop using the UiPath Engineering MCP server. Use when the user asks to build, add, or implement a feature, fix gaps, or work through the plan. Do NOT use for single one-line edits, pure analysis, or documentation generation. Not for Maestro, IXP, Insights, or Agents."
---

# Guided implementation loop for UiPath RPA projects

This skill turns a feature request into a governed loop over the UiPath Engineering MCP
tools. The server is passive — you drive the sequence, one deterministic tool call at a
time. The value of this skill is the **guardrails**: plan first, dry-run before writing,
verify after every task, and stop when told to.

This MCP is **RPA only** (`.xaml` / `.cs`). Decline Maestro, IXP, Insights, Agents,
Orchestrator runtime, and publishing.

## Phase 0 — Scope check (before touching anything)

1. Confirm the target `projectPath` with the user if it is ambiguous. It must be the
   project root (the folder containing `project.json`), inside the configured allowed
   roots.
2. Call `analyze_project` to learn the current project structure, workflow names, and
   file paths. Never guess paths.
3. Restate the goal in one sentence and confirm it with the user if the request is vague.
   Do not start a plan for a requirement you cannot map to concrete workflows/activities.

## Phase 1 — Plan

1. Call `get_implementation_plan` first. If no plan exists, call `create_implementation_plan`
   with the goal and an ordered task list. Each task must be small enough to verify
   independently (one workflow, one edit, one data change).
2. If a plan already exists, resume it — do not call `create_implementation_plan` unless
   none exists. Never pass `overwrite: true` unless the user explicitly asked to replace
   the plan.
3. Present the task list to the user before implementing. Proceed on approval, or on the
   user's original instruction if they already said "implement it".

## Phase 2 — Implement, one task at a time

For each task, in order:

1. Call `update_plan_task` → `in_progress`.
2. New work is **coded** unless the task is REFramework or orchestration XAML.
   XAML may invoke coded workflows with BCL and framework types (including Dictionary, IEnumerable, DataTable, and arrays); never types defined in this automation or source-file methods from XAML.
   - Coded: `add_coded_workflow` (`kind` `workflow` / `test` / `source`); Process `kind=test` defaults to `Tests\`; pass `relativeFolder` for other layouts (empty string forces the project root). Edit `.cs` with `edit_workflow_file` after `read_workflow_file`. `kind=test` registers `fileInfoCollection`, never `entryPoints`.
   - XAML shell: `find_activity` then `insert_activities` for REFramework and `InvokeWorkflowFile` only.
   - Spec-based XAML (full surface): dry-run `validate_activity_spec` before `build_workflow` / `insert_activities`. Do not write files from an invalid spec. Variables/arguments: `manage_workflow_data`.
3. Reminders for specs: shape is `{ name, properties, children, variables (root only),
   catches (TryCatch only) }`; strings in `[expr]` brackets are expressions, everything
   else is a literal; an `If` has no Else branch — `children` is the Then branch.
4. If a tool returns a structured error, read the `fixHint`, correct the call, and retry.
   Do not abandon a task after one failure — but after repeated failures on the same
   task, mark it `blocked` with notes and ask the user instead of guessing.

## Phase 3 — Verify after every task

1. Call `validate_project` with `build: false` and `pack: false`.
2. Confirm the files you wrote with `read_workflow_file` or `search_codebase`.
3. If the task changed file counts or dependencies, call `sync_project_context`.
   If it recorded a decision, call `manage_project_docs` action `write` kind `adr`.
   If it recorded a convention or pitfall, call `manage_project_docs` action `write` kind `memory`.
   If it deleted a feature, call `manage_project_docs` action `delete` (or update `relatedFiles`).
4. Call `update_plan_task` → `done` when validation succeeded and the files exist.
   The plan at `docs/implementation-plan.json` is a scratchpad. Marking done is not blocked on ADR or knowledge freshness.
   On failure, `update_plan_task` → `blocked` with the validation errors in notes.
5. Prefer `validate_project` to close the CLI gate. Do not treat `verify_work` as the green gate.

## Phase 4 — Close out

When all tasks are `done` (or the remaining ones are explicitly `blocked`):

1. Call `validate_project` with `build: false` and `pack: false` for a final project check.
2. Report: what was implemented per task, final validation status, and any blocked tasks
   with their notes. Suggest the next step (e.g. `generate_documentation` or committing).

## Stop rules

- Stop and ask when: the project path is ambiguous; an existing plan would be
  overwritten; a full-file overwrite (`write_workflow_file`) seems necessary; a task
  fails verification repeatedly; or the user interrupts.
- The loop ends when the user's request ends. Do not continue implementing beyond the
  agreed plan without asking.
