---
name: project-docs
description: "Writes and validates UiPath project documentation: ADRs, knowledge articles, AGENTS.md, and Docs/*.md. Use when the user asks for an ADR, architecture decision, knowledge article, AGENTS.md, project context, or documentation. Triggers include 'write an ADR', 'document this decision', 'update AGENTS.md', 'add a knowledge article', 'generate documentation'. Do not use for workflow authoring or multi-step feature implementation. Decline Maestro, IXP, Insights, and Agents."
---

# Project documentation

Writes and checks docs-as-code in a UiPath project through the Engineering MCP tools on the Copilot connector. This MCP is **RPA only**. Decline Maestro, IXP, Insights, Agents, Orchestrator runtime, and publishing.

Workflow create/edit/debug belongs to `rpa-authoring`. Multi-step feature implementation belongs to `guided-implementation-loop`.

## Before writing

1. Confirm `projectPath` (folder that contains `project.json`) inside the allowed roots. Never guess paths. If Memory recalls a `projectPath`, confirm it with `analyze_project` before acting.
2. Call `analyze_project` (`detail=summary`) so related files and workflow names are real.
3. Read existing markdown with `read_workflow_file` or `search_codebase` before overwriting. Never rewrite a redacted credential body (`***REDACTED***`) back to disk.

Do not call `create_implementation_plan`. The implementation plan is not a documentation artifact.

## Which action

Every write goes through `manage_project_content` with `projectPath` and an `action`:

| Target | `action` and arguments |
|--------|------------------------|
| `docs/adr` (architecture decisions) | `write_docs`, `kind=adr`, `title`, `content`, `relatedFiles` (`id` only to update an existing ADR) |
| `docs/knowledge` (conventions, pitfalls) | `write_docs`, `kind=memory`, `id` (kebab-case, required), `title`, `content`, `relatedFiles` |
| List or search existing ADRs / knowledge | `list_docs` or `search_docs` (`query`) |
| Delete an ADR or knowledge article | `delete_docs` with `kind` and `id` |
| `AGENTS.md` and `.claude/rules/project-context.md` | `sync_context` |
| Other project markdown (`Docs\*.md`, notes, wiki pages) | `write_file` / `edit_file` / `delete_file` with `relativePath` |

ADRs need Context, Decision, and Consequences headings. Pass `relatedFiles` for the workflows the article describes. To supersede an ADR, pass `supersedes` with the old ADR id.

The file actions cover `.md` / `.json` / `.txt` that are not `project.json`, implementation-plan files, `docs/knowledge`, or `docs/adr`. `edit_file` takes `oldString` / `newString`; read the file first.

`sync_context` regenerates the marker-spliced `AGENTS.md` block from the current project model. Do not hand-edit the generated region when a sync will do.

## After every write

Call `manage_project_content` with `action=validate_docs`. Fix error findings, then re-validate. Wiki hygiene does not change plan state.

Docs work never blocks RPA `update_plan_task` → `done`: `check_work` already excludes docs findings.

## Read-only sources

`analyze_project` (`detail=summary`) returns the workflow index, packages, risks, and folder tree. `get_workflow_dependencies` returns the invoke graph (edges, cycles, orphans). `explain_workflow` explains one workflow (arguments, variables, outline, handlers). None of them write files.

Summarize those results into the article, then persist it with `manage_project_content` when the user asked for a written doc. Then `validate_docs`.

Canvas snapshot prose (`.canvas/snapshot.json`) is written only with `update_canvas_snapshot`, following the canvas prompts in the prompt pack. Do not write that file with `manage_project_content`.

## Stop

Stop and ask when the project path is ambiguous, the user asked to overwrite a mature ADR without saying so, or `validate_docs` keeps failing after the `fixHint` was applied.
