# JSON activity-spec grammar

Use with `validate_activity_spec` (dry-run), then `build_workflow` or `insert_activities`.

## Spec shape

Each node is JSON:
`{ name, properties, annotation, children, variables, imports, workflowArguments, catches, else, cases, default, arguments, flowchart, stateMachine }`

- `name` — activity type (e.g. `Sequence`, `If`, `InvokeWorkflowFile`).
- `properties` — activity property bag (string keys/values).
- `annotation` — optional designer note.
- `children` — child nodes. For `If`, `children` is the Then branch.
- `variables` — Sequence only (local variables).
- `imports` — root only, C# expression projects.
- `workflowArguments` — root only (workflow in/out/inOut args).
- `catches` — TryCatch only.
- `else` — If Else branch.
- `cases` / `default` — Switch.
- `arguments` — InvokeWorkflowFile and InvokeCode.
- `flowchart` — Flowchart graph payload.
- `stateMachine` — StateMachine graph payload.

## Expression language

Follow `project.json` `expressionLanguage`:

- **VisualBasic** — pass `[expr]` bracket shorthand for expressions; any other value is a literal.
- **CSharp** — pass a raw C# expression with no brackets. Never use bracket shorthand (it deserializes as VB and fails at runtime).

## Container rules

- A root `Sequence` without `variables` inserts its `children` directly (`insert_activities`); any other root is inserted as a single node.
- Prefer `find_activity` for the target `activityId` (WorkflowViewState.IdRef or structural path) before `insert_activities`.
- Run `validate_activity_spec` before writing; violations return structured `errorCode` / `fixHint`.
