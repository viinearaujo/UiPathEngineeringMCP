---
name: ccc
description: "This skill should be used when code search is needed (whether explicitly requested or as part of completing a task), when indexing the codebase after changes, or when the user asks about ccc, cocoindex-code, or the codebase index. Trigger phrases include 'search the codebase', 'find code related to', 'update the index', 'ccc', 'cocoindex-code'."
---

# ccc — Semantic Code Search & Indexing

Prefer `ccc search` for semantic questions ("where is X handled", "code related to Y", exploring unfamiliar areas). Skip `ccc` when a path, filename, or exact string is already known — use Glob/Grep directly.

Do not start `ccc mcp` or call `cocoindex-code` MCP in Cursor — that stdio server hangs agent chat in this workspace.

## Search

```bash
ccc search "<natural language query>"
ccc search --lang csharp "<query>"
ccc search --path "src/*" "<query>"
```

Then `Read` the returned file and line range. Snippets are not a substitute for reading the edit target.

- One search at a time. Never fire parallel `ccc search` / MCP search calls.
- Do not pass `--refresh` unless the user asked to reindex.
- If `ccc` is missing, times out, errors, returns empty/irrelevant hits, or the daemon looks unhealthy, fall back immediately to Grep, Glob, and Task. Do not retry via `cocoindex-code` MCP, do not block the task, and do not wait on MCP.
- If MCP `cocoindex-code` appears in the tool catalog, ignore it. Do not call it. Do not edit `.cursor/mcp.json` to re-enable it.

## Keep the index fresh

- After a batch of significant edits (new/renamed files, substantial logic — not typo/comment tweaks), run `ccc index` once. Do not reindex on every small change or at the start of every chat.
- If a search looks stale vs files just edited, run `ccc doctor`, then `ccc index` if needed.
- `ccc doctor` diagnoses settings, daemon, and index health.
- `ccc daemon status` should stay running; do not run `ccc mcp`.

## Do not use Repowise

Repowise is removed. Do not call `get_answer`, `get_context`, `get_symbol`, `get_risk`, `repowise distill`, or any `repowise` MCP tools.
