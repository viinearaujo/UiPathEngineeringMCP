# MCP File Read & Targeted Edit — Design

Date: 2026-07-29

## Problem

The MCP server has `write_workflow_file` (full overwrite only, `.xaml`/`.cs`) but no
file-read tool. In a real Microsoft 365 Copilot session this meant Copilot could not
retrieve `ConnectAndListSftpSource.cs` or `Data/Config.json` itself — the user had to
paste file contents into the chat, and any fix required a risky full-file overwrite.

## Goal

Let an MCP client (Copilot) read any text file inside an allowed UiPath project and
apply small, targeted edits to workflow files, without the user manually sharing
content and without clobbering untouched code.

## Non-goals

- MCP Resources-based file sharing (uncertain Copilot support, no edit path).
- Generic shell or unrestricted filesystem tools (violates the project's
  no-arbitrary-command rule).
- Changes to `write_workflow_file` behavior.

## Decisions (confirmed with user)

- Read scope: any text file inside the project (not just `.xaml`/`.cs`).
- Edit model: exact string replacement plus the existing full overwrite.
- Secret handling: redact obvious secrets on read rather than returning files as-is.

## Design

### 1. `ReadWorkflowFileTool` — new tool in `UiPath.Engineering.Mcp.Tools`

Signature:

```csharp
ReadWorkflowFile(
    string projectPath,          // must contain project.json
    string relativePath,
    int? startLine = null,       // 1-based
    int? lineCount = null)       // default cap 1000 lines
```

Behavior:

- Guards: `ToolResults.GuardProject` + `ToolResults.TryResolveWithinProject`
  (path traversal refused) — same checks as `WriteWorkflowFileTool`.
- Blocked files: known secret files (`.env`, `.env.*`, `*.pem`, `*.key`,
  paths containing `credentials`) → clear failure.
- Binary detection: NUL-byte sniff of the first bytes → failure explaining the
  file is not text.
- Output: line-numbered content (`<line>\t<text>`) plus metadata
  `{ filePath, totalLines, returnedLines, truncated, redactedCount }`.
- Pagination: `startLine`/`lineCount`; default returns the first 1000 lines with
  `truncated: true` when the file is longer.
- Secret redaction before returning (see §3); `redactedCount` reports how many
  values were masked.

### 2. `EditWorkflowFileTool` — new tool in `UiPath.Engineering.Mcp.Tools`

Signature:

```csharp
EditWorkflowFile(
    string projectPath,
    string relativePath,         // .cs or .xaml only
    string oldString,
    string newString,
    bool replaceAll = false)
```

Behavior:

- Same project/path guards; extension whitelist `.cs`/`.xaml` (mirrors write tool).
- File must exist; `oldString` must be non-empty.
- Match semantics:
  - exactly 1 occurrence → replace, success;
  - 0 occurrences → failure with a whitespace/content hint;
  - >1 occurrences → failure unless `replaceAll: true`, which replaces all and
    reports the count.
- Returns `{ filePath, replacements, bytesWritten }` via `ToolResults.Ok`.

### 3. `SecretRedactor` — static helper in `UiPath.Engineering.Mcp.Core`

- Single regex-based pass over the text.
- Masks values of keys matching (case-insensitive):
  `password|passwd|secret|token|apikey|api_key|accesskey|access_key|connectionstring|connection_string|clientsecret|client_secret|privatekey|private_key`
- Covers JSON (`"key": "value"`) and `key=value` / `key: value` styles; the value
  is replaced with `***REDACTED***`.
- Returns the redacted text plus a mask count.
- Unit-tested in isolation.

### 4. Plumbing

- `IFilesystemProvider.ReadAllText` already exists — no provider change.
- Both tools take `IFilesystemProvider` via constructor injection, following the
  existing tool pattern.
- No `Program.cs` change: `WithToolsFromAssembly(typeof(AnalyzeProjectTool).Assembly)`
  discovers the new tools automatically.

### 5. Error handling

- All failures via `ToolResults.Failure` with elapsed-time metadata, matching
  existing tools. No stack traces or absolute server paths beyond what the
  existing tools already return.

### 6. Testing

New tests in `tests/UiPath.Engineering.Mcp.Tools.Tests` (fake filesystem per
`Fakes.cs` conventions):

- read: returns line-numbered content; pagination (`startLine`/`lineCount`,
  `truncated`); binary file refused; `.env` refused; traversal refused;
  secret values redacted with correct `redactedCount`.
- edit: single-match success; zero-match error; multi-match error without
  `replaceAll`; `replaceAll` replaces all and reports count; wrong extension
  refused; missing file refused.
- `SecretRedactor`: JSON key/value masking, `key=value` masking, non-secret keys
  untouched, count accuracy.

### 7. Docs

- Update the README tools table/list if one exists to include the two new tools.
