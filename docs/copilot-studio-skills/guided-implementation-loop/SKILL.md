---
name: guided-implementation-loop
description: "Implements a UiPath RPA feature (.xaml/.cs) through a governed plan → implement → verify loop on the Engineering MCP. Use when the user asks to build, add, or implement a feature, work through the plan, or fix gaps across multiple tasks. Triggers include 'implement this feature', 'work the plan', 'fix the gaps', 'build the automation'. Do not use for single one-line edits, pure analysis, or documentation generation. Decline Maestro, IXP, Insights, and Agents."
---

# Guided implementation loop

Turns a feature request into a governed loop over the Engineering MCP tools on the Copilot connector. The server is passive — drive the sequence, one deterministic tool call at a time: plan first, dry-run before writing, verify after every task, stop when told.

This MCP is **RPA only** (`.xaml` / `.cs`). Decline Maestro, IXP, Insights, Agents, Orchestrator runtime, and publishing.

Single-file create/edit/debug without a multi-step plan belongs to `rpa-authoring`. ADRs, knowledge, `AGENTS.md`, and `Docs\*.md` belong to `project-docs`.

## Phase 0 — Scope check

1. Confirm `projectPath` (folder that contains `project.json`) inside the allowed roots. Never guess paths. If Memory recalls a `projectPath` or plan state, confirm it with `analyze_project` / `get_implementation_plan` before acting.
2. Call `analyze_project` (`detail=summary`) for structure, workflow names, and file paths.
3. Restate the goal in one sentence and confirm it if the request is vague. Do not start a plan for a requirement that cannot map to concrete workflows or activities.

## Phase 1 — Plan

1. Call `get_implementation_plan` first.
2. If no plan exists, call `create_implementation_plan` with the goal and an ordered task list. Each task must be small enough to verify independently (one workflow, one edit, one data change).
3. If a plan already exists, resume it. Do not call `create_implementation_plan` unless none exists. Never pass `overwrite: true` unless the user explicitly asked to replace the plan.
4. Present the task list before implementing. Proceed on approval, or on the user's original instruction if they already said "implement it".

## Phase 2 — Implement, one task at a time

For each task, in order:

1. Call `update_plan_task` → `in_progress`. Call `checkpoint` before a risky edit.
2. New work is **coded** unless the task is REFramework or orchestration XAML. Match the idioms bundled with this skill: `references/idioms/coded-workflow-try-log.md`, `references/idioms/coded-testcase.md`, and `references/idioms/thin-reframework-invoke.md`. A project's own `docs/idioms/` files, when present, override them.
   XAML may invoke coded workflows with BCL and framework types (including Dictionary, IEnumerable, DataTable, and arrays); never types defined in this automation or source-file methods from XAML.
   - Coded: `add_coded_workflow` (`className`, `kind` `workflow` / `test` / `source`); Process `kind=test` defaults to `Tests\`; pass `relativeFolder` for other layouts (empty string forces the project root). Edit `.cs` with `edit_workflow_file` after `read_workflow_file`. `kind=test` registers `fileInfoCollection`, never `entryPoints`. Navigate symbols with `navigate_code` (`mode=symbol` / `context` / `references`).
   - XAML shell: `find_activity` then `insert_activities` for REFramework and `InvokeWorkflowFile` only. New XAML file: `build_workflow` from a minimal spec such as `{ "name": "Sequence" }`.
   - Spec-based XAML (full surface): read `references/activity-spec.md`, bundled with this skill, for the grammar and the expression-language rules. Look up unknown activities with `search_knowledge` (`mode=activity_docs`). Dry-run `validate_activity_spec` with `projectPath` before `build_workflow` / `insert_activities`. Do not write files from an invalid spec. Variables/arguments: `manage_workflow_data`.
3. UI steps without live capture: real UIA activities with placeholder selectors and `TODO Indicate` markers. Read the Placeholder-Selector Stub Pattern with `search_knowledge` (`mode=skill`, `query=uipath-rpa`, `file=references/uia-starter-guide.md`). Do not emit `Log` stubs.
4. If a tool returns a structured error, read the `fixHint`, correct the call, and retry. After repeated failures on the same task, mark it `blocked` with notes and ask the user instead of guessing.

Deep coded/XAML policy: `search_knowledge` (`mode=skill`, `query=uipath-rpa`) with `file=references/coded/operations-guide.md` or `file=references/xaml/xaml-basics-and-rules.md`.

## Phase 3 — Verify after every task

After **one** task:

1. Confirm the files you wrote with `read_workflow_file` or `search_codebase`. Never rewrite a redacted credential body (`***REDACTED***`) back to disk.
2. Call `check_work` with `projectPath` and `files` set to the project-relative files this task touched. One call returns the Roslyn compile, `validate`, and scoped gaps (top 20). Remediate resilience, observability, structure, and coded/XAML boundary issues. Docs freshness is not part of the verdict.
3. Call `update_plan_task` → `done` when `check_work` passed and the files exist.
   The plan at `docs/implementation-plan.json` is a scratchpad. Marking done is not blocked on ADR or knowledge freshness.
   On failure, `update_plan_task` → `blocked` with the `check_work` issues in notes. `get_changes` / `revert_changes` undo the task's edits when the user asks.

Incidental docs (do not block done): file-count or dependency changes → `manage_project_content` action `sync_context`; a recorded decision → action `write_docs` kind `adr`; a convention or pitfall → action `write_docs` kind `memory`.

## Phase 4 — Close out

When all tasks are `done` (or the remaining ones are explicitly `blocked`):

1. Call `check_work` with `projectPath` and no `files` for a final project-wide verdict. Start `validate_project` with `build: true` (then poll `get_job`) only when the user asks for an authoritative CLI compile.
2. Report: what was implemented per task, final `check_work` status, and any blocked tasks with their notes. Suggest the next step (`explain_workflow` for a walkthrough, or committing).

## Stop rules

- Stop and ask when: the project path is ambiguous; an existing plan would be overwritten; a wholesale file rewrite seems necessary; a task fails verification repeatedly; or the user interrupts.
- Surgical `.cs` edits go through `edit_workflow_file`; XAML inserts go through `insert_activities`.
- The loop ends when the user's request ends. Do not continue implementing beyond the agreed plan without asking.
