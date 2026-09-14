---
name: project-docs
description: "Writes and validates UiPath project documentation: ADRs, knowledge articles, AGENTS.md, and Docs/*.md. Use when the user asks for an ADR, architecture decision, knowledge article, AGENTS.md, project context, or documentation. Triggers include 'write an ADR', 'document this decision', 'update AGENTS.md', 'add a knowledge article', 'generate documentation'. Do not use for workflow authoring or multi-step feature implementation. Decline Maestro, IXP, Insights, and Agents."
---

# Project documentation

Writes and checks docs-as-code in a UiPath project through advertised Engineering MCP tools. This MCP is **RPA only**. Decline Maestro, IXP, Insights, Agents, Orchestrator runtime, and publishing.

Workflow create/edit/debug belongs to `rpa-authoring`. Multi-step feature implementation belongs to `guided-implementation-loop`.

## Before writing

1. Confirm `projectPath` (folder that contains `project.json`) inside the allowed roots. Never guess paths.
2. Call `analyze_project` (`detail=summary`) so related files and workflow names are real.
3. Read existing markdown with `read_workflow_file` or `search_codebase` before overwriting. Never rewrite a redacted credential body (`***REDACTED***`) back to disk.

Do not call `create_implementation_plan`. The implementation plan is not a documentation artifact.

## Which write tool

| Target | Tool |
|--------|------|
| `docs/adr` (architecture decisions) | `manage_project_docs` action `write` kind `adr` |
| `docs/knowledge` (conventions, pitfalls) | `manage_project_docs` action `write` kind `memory` |
| List or search existing ADRs / knowledge | `manage_project_docs` action `list` or `search` |
| Delete an ADR or knowledge article | `manage_project_docs` action `delete` |
| `AGENTS.md` and `.claude/rules/project-context.md` | `sync_project_context` |
| Other project markdown (`Docs\*.md`, notes, wiki pages) | `manage_project_file` |

`manage_project_docs` ADRs need Context, Decision, and Consequences headings. Pass `relatedFiles` for the workflows the article describes.

`manage_project_file` covers `.md` / `.json` / `.txt` that are not `project.json`, implementation-plan files, `docs/knowledge`, or `docs/adr`. Use `action` `write`, `edit`, or `delete`.

`sync_project_context` regenerates the marker-spliced `AGENTS.md` block from the current project model. Do not hand-edit the generated region when a sync will do.

## After every write

Call `validate_project_docs`. Fix error findings, then re-validate. Wiki hygiene does not change plan state.

If `analyze_project_gaps` is in the conversation, **ignore `category=docs`**. Docs work must not block RPA `update_plan_task` → `done`.

## Read-only dumps

`generate_documentation` returns structured project documentation data (metadata, per-workflow summaries, dependency graph, risks). It does not write files.

`explain_workflow` explains one workflow (arguments, variables, outline, handlers). It does not write files.

Paste or file those dumps with `manage_project_file` or `manage_project_docs` when the user asked to persist them. Then `validate_project_docs`.

## Stop

Stop and ask when the project path is ambiguous, the user asked to overwrite a mature ADR without saying so, or `validate_project_docs` keeps failing after the `fixHint` was applied.
