# Copilot prompt pack

Copy-paste user messages for Copilot Studio. They are **not** agent instructions.

Follow [copilot-studio-agent-instructions.txt](copilot-studio-agent-instructions.txt) for the authoring loop. Each template below is the intent for this chat.

## Setup (human)

1. Copy the markdown files in [copilot-idioms/](copilot-idioms/) into the **target** UiPath project at `docs/idioms/` (or another project-relative folder). `read_workflow_file` only opens files inside an allowed project (`project.json` + `Projects:AllowedRoots`). They cannot live only in this MCP repo.
2. Replace the placeholders. You may attach the same idiom files or a markdown plan in the Copilot chat; still pass `{PROJECT_PATH}` so tools can write.
3. Paste one template into the Copilot user message.

Grounding is a local path plus `read_workflow_file`. Do not add SharePoint or Dataverse as Copilot knowledge.

## Shared placeholders

| Placeholder | Required | Meaning |
|-------------|----------|---------|
| `{PROJECT_PATH}` | yes | Folder that contains `project.json` |
| `{GOAL}` | yes | What to build, change, debug, or document |
| `{PLAN_MD}` | no | Project-relative path, usually `docs/implementation-plan.md` (the markdown mirror next to `docs/implementation-plan.json`). You can paste the markdown into the message instead. |
| `{IDIOM_DIR}` | no | Project-relative folder of idiom samples. Default: `docs/idioms` |

## Optional plan (`{PLAN_MD}` vs JSON scratchpad)

- If `{PLAN_MD}` or an attached markdown plan is present: `read_workflow_file` it first.
- If `docs/implementation-plan.json` exists: also `get_implementation_plan` and follow that JSON scratchpad (`update_plan_task` as you go). Never overwrite a mature JSON plan.
- If only markdown exists (hand-written, no JSON): treat that markdown as this session’s task list. Do **not** call `create_implementation_plan` (leave-off).
- If no plan: continue without one.

## Idiom files in `{IDIOM_DIR}`

Shipped samples (copy into the target project):

| File | Match this shape |
|------|------------------|
| `coded-workflow-try-log.md` | Coded `[Workflow]` with `try` / `Log` on the entry method |
| `thin-reframework-invoke.md` | Thin REFramework / `InvokeWorkflowFile` shell, BCL/framework args only |
| `coded-testcase.md` | Coded `[TestCase]` (Arrange / Act / Assert) |

If `{IDIOM_DIR}` exists, `read_workflow_file` those files first and match them. If the folder is missing, skip — do not invent a second playbook.

---

## 1. New feature

```
Follow the Copilot agent instructions; this message is the intent.

projectPath: {PROJECT_PATH}
goal: {GOAL}
planMd: {PLAN_MD}
idiomDir: {IDIOM_DIR}

Plan: If planMd or attached markdown is present, read_workflow_file it first. If docs/implementation-plan.json exists, also get_implementation_plan and follow that JSON scratchpad with update_plan_task. If only markdown exists, treat it as this session's task list. Do not call create_implementation_plan. Never overwrite a mature JSON plan. If no plan, continue without one.

Idioms: If idiomDir exists (default docs/idioms), read_workflow_file those files first and match them.

Intent — new feature:
- analyze_project (detail=summary).
- Author coded-first: add_coded_workflow for business logic; keep XAML as a thin REFramework / InvokeWorkflowFile shell (find_activity + insert_activities only). Match idiomDir if present.
- After writes: validate_project(build:false, pack:false), then analyze_project_gaps. Remediate resilience, observability, structure, and coded/XAML boundary gaps before done. Ignore category=docs.
- If a JSON plan is in play, update_plan_task as you go.
```

---

## 2. Change existing feature

```
Follow the Copilot agent instructions; this message is the intent.

projectPath: {PROJECT_PATH}
goal: {GOAL}
planMd: {PLAN_MD}
idiomDir: {IDIOM_DIR}

Plan: If planMd or attached markdown is present, read_workflow_file it first. If docs/implementation-plan.json exists, also get_implementation_plan and follow that JSON scratchpad with update_plan_task. If only markdown exists, treat it as this session's task list. Do not call create_implementation_plan. Never overwrite a mature JSON plan. If no plan, continue without one.

Idioms: If idiomDir exists (default docs/idioms), read_workflow_file those files first and match them.

Intent — change existing:
- analyze_project, then search_codebase / read_workflow_file the files you will touch. Do not write until you have the current text.
- Surgical edits only: edit_workflow_file for .cs; find_activity + insert_activities for REFramework / InvokeWorkflowFile wiring only.
- After writes: validate_project(build:false, pack:false), then analyze_project_gaps. Remediate resilience, observability, structure, and coded/XAML boundary gaps before done. Ignore category=docs.
- If a JSON plan is in play, update_plan_task as you go.
```

---

## 3. Debug

```
Follow the Copilot agent instructions; this message is the intent.

projectPath: {PROJECT_PATH}
goal: {GOAL}
planMd: {PLAN_MD}
idiomDir: {IDIOM_DIR}

Plan: If planMd or attached markdown is present, read_workflow_file it first. If docs/implementation-plan.json exists, also get_implementation_plan and follow that JSON scratchpad with update_plan_task. If only markdown exists, treat it as this session's task list. Do not call create_implementation_plan. Never overwrite a mature JSON plan. If no plan, continue without one.

Idioms: If idiomDir exists (default docs/idioms), read_workflow_file those files first when a fix must match project shape.

Intent — debug:
- Do not author new workflows.
- Diagnose with get_compile_errors, validate_project(build:false, pack:false), search_codebase, and read_workflow_file.
- Report the cause. On structured tool errors, apply fixHint and retry. Only edit after the failure is identified.
- If docs/implementation-plan.json exists, update_plan_task the failing task to blocked with the cause in notes. Do not add a new plan. If only markdown exists, note the blocker against that task list in your reply.
```

---

## 4. Update project documentation

Default connector **cannot** write knowledge, ADRs, or AGENTS.md. It cannot call `generate_documentation`, `sync_project_context`, `manage_project_file`, `manage_project_docs`, or `validate_project_docs` (all leave-off). Pick one mode.

### Default tools (read, then paste)

```
Follow the Copilot agent instructions; this message is the intent.

projectPath: {PROJECT_PATH}
goal: {GOAL}
planMd: {PLAN_MD}
idiomDir: {IDIOM_DIR}
docsMode: default-tools

Plan: If planMd or attached markdown is present, read_workflow_file it first. If docs/implementation-plan.json exists, also get_implementation_plan (read-only context). If only markdown exists, treat it as this session's outline. Do not call create_implementation_plan. Never overwrite a mature JSON plan. If no plan, continue without one.

Intent — update project documentation (default tools):
- Read with analyze_project, search_codebase, and read_workflow_file.
- Return markdown the user can paste into AGENTS.md or a wiki.
- Do not invent tool writes that are not on the default connector. Do not call generate_documentation, sync_project_context, manage_project_file, manage_project_docs, or validate_project_docs.
```

### Docs extras (enable on the connector for this chat)

Enable these four in addition to (or instead of) unused default-connector slots so Copilot can write files:

`generate_documentation`, `sync_project_context`, `manage_project_docs`, `validate_project_docs`

```
Follow the Copilot agent instructions; this message is the intent.

projectPath: {PROJECT_PATH}
goal: {GOAL}
planMd: {PLAN_MD}
idiomDir: {IDIOM_DIR}
docsMode: docs-extras

Plan: If planMd or attached markdown is present, read_workflow_file it first. If docs/implementation-plan.json exists, also get_implementation_plan (read-only context unless a docs task is already on that scratchpad). If only markdown exists, treat it as this session's outline. Do not call create_implementation_plan. Never overwrite a mature JSON plan. If no plan, continue without one.

Intent — update project documentation (docs extras):
- You may call generate_documentation, sync_project_context, manage_project_docs, and validate_project_docs because they are enabled on this connector for this chat.
- Read first (analyze_project / search_codebase / read_workflow_file), then write project docs with those extras.
- After writes, validate_project_docs. Do not call create_implementation_plan.
```
