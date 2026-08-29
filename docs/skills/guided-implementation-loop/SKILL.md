---
name: guided-implementation-loop
description: "Implement a feature or change request in a UiPath project through a governed plan → implement → verify loop using the UiPath Engineering MCP server. Use this whenever the user asks to build, add, or implement a feature, fix a set of gaps, or carry out multi-step work in a UiPath project — especially anything phrased as 'implement X', 'add feature X', 'finish the remaining work', or 'work through the plan'. Do NOT use for single one-line edits, pure analysis/review requests, or documentation generation."
---

# Guided implementation loop for UiPath projects

This skill turns a feature request into a governed loop over the UiPath Engineering MCP
tools. The server is passive — you drive the sequence, one deterministic tool call at a
time. The value of this skill is the **guardrails**: plan first, dry-run before writing,
verify after every task, and stop when told to.

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
2. Author with the **spec-based tools** — never hand-write XAML:
   - Always dry-run first: `validate_activity_spec`. Fix every reported violation using
     its `fixHint` and re-dry-run until clean. Do not write files from an invalid spec.
   - New workflow: `build_workflow` (or `add_xaml_workflow` then `insert_activities`).
   - Extend an existing workflow: `insert_activities` under the target `DisplayName`.
   - Variables/arguments: `manage_workflow_data`.
   - Coded workflows: `add_coded_workflow`; edit `.cs` content with `edit_workflow_file`
     after reading it with `read_workflow_file`.
3. Reminders for specs: shape is `{ name, properties, children, variables (root only),
   catches (TryCatch only) }`; strings in `[expr]` brackets are expressions, everything
   else is a literal; an `If` has no Else branch — `children` is the Then branch.
4. If a tool returns a structured error, read the `fixHint`, correct the call, and retry.
   Do not abandon a task after one failure — but after repeated failures on the same
   task, mark it `blocked` with notes and ask the user instead of guessing.

## Phase 3 — Verify after every task

1. Call `validate_project` with `build: false` and `pack: false`.
2. Confirm the files you wrote with `read_workflow_file` or `search_codebase`.
3. Call `update_plan_task` → `done` only when validation succeeded and the files exist.
   On failure, `update_plan_task` → `blocked` with the validation errors in notes.
4. Do not call `verify_work` to close the task (it forces CLI build).

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
