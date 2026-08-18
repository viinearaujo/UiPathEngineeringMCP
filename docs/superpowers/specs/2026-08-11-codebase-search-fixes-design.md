# CodebaseSearchService Fixes — Design

Date: 2026-08-11
Status: Approved (design)

## Scope

Three targeted fixes in `src/UiPath.Engineering.Mcp.Core/CodeSearch/CodebaseSearchService.cs`,
plus the `IFilesystemProvider` contract change the first fix requires. No changes to
`ICodebaseSearchService` or the tools layer.

## 1. Pre-read size guard via `GetFileSize`

**Problem:** `SearchTextAsync` reads each file fully (`ReadAllText`) and only then rejects it
when `content.Length > MaxFileCharacters` (2,000,000 chars). An oversized file is loaded into
memory before being skipped.

**Change:**

- `IFilesystemProvider` (`src/UiPath.Engineering.Mcp.Core/Abstractions/IFilesystemProvider.cs`)
  gains:
  ```csharp
  long GetFileSize(string filePath);
  ```
- `FilesystemProvider` (`src/UiPath.Engineering.Mcp.Providers/Filesystem/FilesystemProvider.cs`)
  implements it as `new FileInfo(Path.GetFullPath(filePath)).Length` (byte length).
- `SearchTextAsync` checks `GetFileSize(file) > MaxFileCharacters` **before** calling
  `ReadAllText`. Oversized files go to `SkippedFiles` with the existing warning shape; the
  warning message reports bytes (e.g. `Skipped oversized file '...' (N bytes).`).
- The existing post-read char check is kept as a safety net (implementations may disagree;
  chars ≤ bytes for UTF-8).

**Trade-off (accepted):** the pre-read check compares *bytes* against a *character* budget.
A file over 2 MB on disk but under 2M chars (multibyte-heavy UTF-8, e.g. CJK) that is searched
today will now be skipped with a warning.

**Test fakes:**

- `tests/UiPath.Engineering.Mcp.Core.Tests/FakeFilesystemProvider.cs`: size derives from
  `FileContents[path].Length`; throws `FileNotFoundException` for unknown paths (matching its
  `ReadAllText`). Gains a `Dictionary<string, long> FileSizes` override so tests can set a size
  independent of content.
- `tests/UiPath.Engineering.Mcp.Tools.Tests/Fakes.cs` `FakeFilesystemProvider`: same pattern
  (content length with override), defaulting to `ProjectJsonContent.Length` for unknown paths
  to match its `ReadAllText` fallback.

## 2. Cancellation inside `EnumerateSourceSymbols`

**Problem:** the recursive lazy iterator `EnumerateSourceSymbols` performs no cancellation
checks, so a symbol search over a large compilation ignores the caller's token until
enumeration completes.

**Change:**

- Add a `CancellationToken` parameter to `EnumerateSourceSymbols`; call
  `cancellationToken.ThrowIfCancellationRequested()` at the top of each `GetMembers()` loop
  iteration; pass the token through the recursive calls.
- `SearchSymbolsAsync` passes its `cancellationToken` at the call site (line ~97).

## 3. Redundant `FileNotFoundException` filter

**Problem:** `CodebaseSearchService.cs:42` filters
`ex is IOException or UnauthorizedAccessException or FileNotFoundException`.
`FileNotFoundException` derives from `IOException`, so the last alternative is redundant.

**Change:** remove `or FileNotFoundException`. Behavior unchanged.

## Testing

- All existing tests stay green. `SearchText_OversizedFile_SkippedWithWarning` still passes
  (fake size derives from content; it asserts only `Assert.Single(result.Warnings)`, not the
  message text, so the byte-based message needs no test update).
- `SearchText_UnreadableFile_SkippedWithWarning` covers fix 3 unchanged.
- New: pre-read skip test — fake `FileSizes` override marks a file oversized while its content
  is small; assert the file lands in `SkippedFiles` and `FilesSearched` does not count it.
- New: cancellation test — `SearchSymbolsAsync` with an already-cancelled token throws
  `OperationCanceledException` (requires a stub `ICSharpContextBuilder` returning a minimal
  context with `HasCSharpFiles = true`).
- New: `FilesystemProviderTests` coverage for `GetFileSize` against a real temp file.

## Out of scope

- Streaming/capped reads (rejected in favor of `GetFileSize`).
- `.WithCancellation(token)` at the call site (rejected in favor of an explicit token
  parameter inside the iterator).
- Any changes to other `ReadAllText` callers.
