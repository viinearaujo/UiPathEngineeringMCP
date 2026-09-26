# Copilot prompt pack

Copy-paste user messages for Copilot Studio. They are **not** agent instructions.

Follow [copilot-studio-agent-instructions.txt](copilot-studio-agent-instructions.txt) for the authoring loop. Each template below is the intent for this chat. Uploaded Copilot Studio skills plus the default connector catalog are the Copilot path.

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
- If `docs/implementation-plan.json` exists: also `get_implementation_plan` and follow that JSON scratchpad (`update_plan_task` as you go). Never overwrite a mature JSON plan unless the user asked.
- If only markdown exists (hand-written, no JSON): treat that markdown as this session’s task list. You may call `create_implementation_plan` only when `docs/implementation-plan.json` is missing.
- If no plan: `create_implementation_plan` for a multi-step goal, or continue without one for a small change.

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

Plan: If planMd or attached markdown is present, read_workflow_file it first. If docs/implementation-plan.json exists, also get_implementation_plan and follow that JSON scratchpad with update_plan_task. If none exists, create_implementation_plan. Never overwrite a mature JSON plan unless the user asked. If only markdown exists, treat it as this session's task list. If no plan, continue without one for a small change.

Idioms: If idiomDir exists (default docs/idioms), read_workflow_file those files first and match them.

Intent — new feature:
- analyze_project (detail=summary).
- Author coded-first: add_coded_workflow for business logic; keep XAML as a thin REFramework / InvokeWorkflowFile shell (find_activity + insert_activities, or recommend_activities → validate_activity_spec → build_workflow / insert_activities + manage_workflow_data). Match idiomDir if present.
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

Plan: If planMd or attached markdown is present, read_workflow_file it first. If docs/implementation-plan.json exists, also get_implementation_plan and follow that JSON scratchpad with update_plan_task. If none exists, create_implementation_plan. Never overwrite a mature JSON plan unless the user asked. If only markdown exists, treat it as this session's task list. If no plan, continue without one for a small change.

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

Plan: If planMd or attached markdown is present, read_workflow_file it first. If docs/implementation-plan.json exists, also get_implementation_plan and follow that JSON scratchpad with update_plan_task. If only markdown exists, treat it as this session's task list. Never overwrite a mature JSON plan unless the user asked. If no plan, continue without one.

Idioms: If idiomDir exists (default docs/idioms), read_workflow_file those files first when a fix must match project shape.

Intent — debug:
- Do not author new workflows.
- Diagnose with get_compile_errors, validate_project(build:false, pack:false), search_codebase, read_workflow_file, find_code_symbol, find_code_references, and get_code_context.
- Report the cause. On structured tool errors, apply fixHint and retry. Only edit after the failure is identified.
- If docs/implementation-plan.json exists, update_plan_task the failing task to blocked with the cause in notes. Do not add a new plan. If only markdown exists, note the blocker against that task list in your reply.
```

---

## 4. Update project documentation

Docs extras (`generate_documentation`, `sync_project_context`, `manage_project_file`, `manage_project_docs`, `validate_project_docs`) are on the default connector.

```
Follow the Copilot agent instructions; this message is the intent.

projectPath: {PROJECT_PATH}
goal: {GOAL}
planMd: {PLAN_MD}
idiomDir: {IDIOM_DIR}

Plan: If planMd or attached markdown is present, read_workflow_file it first. If docs/implementation-plan.json exists, also get_implementation_plan (read-only context unless a docs task is already on that scratchpad). If only markdown exists, treat it as this session's outline. Never overwrite a mature JSON plan unless the user asked. If no plan, continue without one.

Intent — update project documentation:
- Read first with analyze_project, search_codebase, read_workflow_file, generate_documentation, and explain_workflow.
- Write knowledge and ADRs with manage_project_docs (docs/adr, docs/knowledge). Write other markdown with manage_project_file. Regenerate AGENTS.md with sync_project_context.
- After writes, validate_project_docs. Ignore category=docs on analyze_project_gaps so docs work does not block RPA done.
```

---

## 5. Canvas snapshot

`generate_documentation` is leave-off. Enable it before the skeleton template. `update_canvas_snapshot` is on the default connector. Do not read `.canvas/snapshot.json` into the chat. Do not edit that file with `manage_project_content`.

### 5.1 Skeleton

```
Follow the Copilot agent instructions; this message is the intent.

projectPath: {PROJECT_PATH}

Intent — canvas skeleton:
- Enable generate_documentation for this pass (ToolSurface=All or the Copilot Studio toggle).
- Call generate_documentation once with format "canvasSnapshot" on projectPath.
- Do not call explain_workflow. Do not call update_canvas_snapshot. Do not write overview, explanation, or decisions.
- Stop. Report the written path, node count, edge count, and generatedAt.
- Call canvasSnapshot again only when the user asks to refresh facts. That call replaces prose already in the file.
```

### 5.2 Overview

```
Follow the Copilot agent instructions; this message is the intent.

projectPath: {PROJECT_PATH}

Intent — canvas overview:
- Do not call generate_documentation. Do not call explain_workflow.
- Read the project with analyze_project (detail=summary) and project-wide get_workflow_dependencies.
- Call update_canvas_snapshot once with projectPath and overview set to a short plain-language summary of what the process does and how work flows from the entry point. Put no secrets, tokens, passwords, connection strings, or credential-bearing URLs in overview.
- Stop.
```

### 5.3 Next workflows

```
Follow the Copilot agent instructions; this message is the intent.

projectPath: {PROJECT_PATH}

Intent — canvas explanations:
- Do not call generate_documentation. Do not read .canvas/snapshot.json into the chat. Do not use manage_project_content on that file.
- Call update_canvas_snapshot with only projectPath. If it reports the snapshot does not exist, stop and say to run the skeleton prompt.
- If remainingCount is 0, stop and say every workflow explanation is written.
- Otherwise take at most 3 ids from nextNodeIds. For each id, one at a time:
  - explain_workflow with projectPath and workflowFile set to that id. Do not pass includeActivityTree.
  - From Activities, collect DisplayName where Type is If, FlowDecision, Switch, or Pick, in list order. Use [] when none match. Coded workflows always use [].
  - Write a short explanation of that workflow's logic, arguments, and decisions. Put no secrets, tokens, passwords, connection strings, or credential-bearing URLs in the explanation or decision names.
  - update_canvas_snapshot for that single node (id, explanation, decisions). Wait for success before the next id.
- After those nodes, stop. Report explainedCount / totalCount. The next message, including a new chat, is this same template.
```
