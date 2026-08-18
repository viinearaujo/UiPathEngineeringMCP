# CodebaseSearchService Fixes Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix three issues in `CodebaseSearchService`: read the file size before reading content (pre-read 2 MB guard), honor cancellation during symbol enumeration, and drop a redundant exception-filter alternative.

**Architecture:** Add `GetFileSize(string)` to `IFilesystemProvider` (real provider + both test fakes), use it in `SearchTextAsync` to skip oversized files before reading, thread a `CancellationToken` through the private recursive iterator `EnumerateSourceSymbols`, and simplify the catch filter since `FileNotFoundException` derives from `IOException`.

**Tech Stack:** C# / .NET 8, xunit, Roslyn (`Microsoft.CodeAnalysis`), existing `IFilesystemProvider` abstraction.

**Spec:** `docs/superpowers/specs/2026-08-11-codebase-search-fixes-design.md`

## Global Constraints

- Do not change `ICodebaseSearchService` or anything in `src/UiPath.Engineering.Mcp.Tools`.
- Keep the existing post-read char-count check in `SearchTextAsync` as a safety net.
- `CodebaseSearchService.MaxFileCharacters` is `internal` and Core.Tests has no `InternalsVisibleTo`; tests must use the literal `2_000_001`.
- Warning message shapes: `Skipped oversized file '<path>' (<n> bytes).` (pre-read) and `Skipped oversized file '<path>' (<n> characters).` (post-read), `Skipped unreadable file '<path>': <msg>` (unchanged).
- Git commits: the environment forbids unsolicited git mutations — ask the user before running each `git commit` step.
- Run tests from the repo root: `dotnet test tests/<project>` (Git Bash environment).

---

### Task 1: `GetFileSize` on `IFilesystemProvider` (contract + real provider + fakes)

**Files:**
- Modify: `src/UiPath.Engineering.Mcp.Core/Abstractions/IFilesystemProvider.cs`
- Modify: `src/UiPath.Engineering.Mcp.Providers/Filesystem/FilesystemProvider.cs` (add member after `ReadAllText`, line 161)
- Modify: `tests/UiPath.Engineering.Mcp.Core.Tests/FakeFilesystemProvider.cs`
- Modify: `tests/UiPath.Engineering.Mcp.Tools.Tests/Fakes.cs` (`FakeFilesystemProvider`, lines 13-36)
- Test: `tests/UiPath.Engineering.Mcp.Providers.Tests/FilesystemProviderTests.cs`

**Interfaces:**
- Consumes: nothing new.
- Produces: `long IFilesystemProvider.GetFileSize(string filePath)` — byte length of the file; throws `FileNotFoundException` when the file does not exist. Used by Task 2 in `SearchTextAsync`.

- [ ] **Step 1: Write the failing test**

Append to `FilesystemProviderTests` (before the private `TempDir` class at the end of the file):

```csharp
[Fact]
public void GetFileSize_ReturnsByteLength() {
    using var temp = new TempDir();
    var target = Path.Combine(temp.Path, "Main.xaml");
    File.WriteAllText(target, "<x/>");
    var sut = CreateSut(temp.Path);

    Assert.Equal(new FileInfo(target).Length, sut.GetFileSize(target));
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/UiPath.Engineering.Mcp.Providers.Tests --filter "FullyQualifiedName~GetFileSize"`
Expected: build FAIL — `FilesystemProvider` does not implement `GetFileSize` (interface member added in Step 3 is required for it to compile; if you add the interface member first, the failure is "FilesystemProvider does not implement interface member").

- [ ] **Step 3: Add the interface member**

In `src/UiPath.Engineering.Mcp.Core/Abstractions/IFilesystemProvider.cs`, after `string ReadAllText(string filePath);`:

```csharp
long GetFileSize(string filePath);
```

- [ ] **Step 4: Implement in the real provider**

In `src/UiPath.Engineering.Mcp.Providers/Filesystem/FilesystemProvider.cs`, after `ReadAllText` (line 161):

```csharp
public long GetFileSize(string filePath) => new FileInfo(Path.GetFullPath(filePath)).Length;
```

- [ ] **Step 5: Implement in the Core test fake**

In `tests/UiPath.Engineering.Mcp.Core.Tests/FakeFilesystemProvider.cs`, add the override dictionary next to the other collections and the member after `ReadAllText`:

```csharp
public Dictionary<string, long> FileSizes { get; } = new(StringComparer.OrdinalIgnoreCase);

public long GetFileSize(string filePath) {
    if (FileSizes.TryGetValue(filePath, out var size)) {
        return size;
    }
    return FileContents.TryGetValue(filePath, out var content)
        ? content.Length
        : throw new FileNotFoundException(filePath);
}
```

The `FileSizes` override lets Task 2's test make size and content disagree.

- [ ] **Step 6: Implement in the Tools test fake**

In `tests/UiPath.Engineering.Mcp.Tools.Tests/Fakes.cs`, in `FakeFilesystemProvider`, add the override dictionary next to the other collections and the member after `ReadAllText` (line 28), matching that fake's `ProjectJsonContent` fallback:

```csharp
public Dictionary<string, long> FileSizes { get; } = new(StringComparer.OrdinalIgnoreCase);

public long GetFileSize(string filePath) {
    if (FileSizes.TryGetValue(filePath, out var size)) {
        return size;
    }
    return FileContents.TryGetValue(filePath, out var content) ? content.Length : ProjectJsonContent.Length;
}
```

- [ ] **Step 7: Run test to verify it passes**

Run: `dotnet test tests/UiPath.Engineering.Mcp.Providers.Tests --filter "FullyQualifiedName~GetFileSize"`
Expected: PASS

- [ ] **Step 8: Run the full suite to catch any other `IFilesystemProvider` implementers**

Run: `dotnet test`
Expected: all PASS (build succeeds — no other implementers exist)

- [ ] **Step 9: Commit**

```bash
git add src/UiPath.Engineering.Mcp.Core/Abstractions/IFilesystemProvider.cs src/UiPath.Engineering.Mcp.Providers/Filesystem/FilesystemProvider.cs tests/UiPath.Engineering.Mcp.Core.Tests/FakeFilesystemProvider.cs tests/UiPath.Engineering.Mcp.Tools.Tests/Fakes.cs tests/UiPath.Engineering.Mcp.Providers.Tests/FilesystemProviderTests.cs
git commit -m "feat: add GetFileSize to IFilesystemProvider"
```

(Ask the user before committing — see Global Constraints.)

---

### Task 2: Pre-read size guard in `SearchTextAsync` (+ drop redundant exception filter)

**Files:**
- Modify: `src/UiPath.Engineering.Mcp.Core/CodeSearch/CodebaseSearchService.cs:37-51`
- Test: `tests/UiPath.Engineering.Mcp.Core.Tests/SearchTextTests.cs`

**Interfaces:**
- Consumes: `long IFilesystemProvider.GetFileSize(string filePath)` from Task 1; `FakeFilesystemProvider.FileSizes` override from Task 1.
- Produces: nothing consumed by later tasks.

Note: this task also removes the redundant `or FileNotFoundException` from the catch filter (`FileNotFoundException : IOException`). The rewritten catch below already has it removed; existing test `SearchText_UnreadableFile_SkippedWithWarning` covers the behavior (fake `ReadAllText`/`GetFileSize` throw `FileNotFoundException` for unknown paths).

- [ ] **Step 1: Write the failing test**

Append to `SearchTextTests`:

```csharp
[Fact]
public async Task SearchText_OversizedFileBySize_SkippedBeforeReading() {
    var fs = new FakeFilesystemProvider();
    fs.XamlFiles.Add(MainXaml);
    fs.FileSizes[MainXaml] = 2_000_001L; // size says oversized...
    fs.FileContents[MainXaml] = "queue"; // ...even though the content is tiny
    var sut = CreateService(fs);

    var result = await sut.SearchTextAsync(Root, "queue");

    Assert.Equal([MainXaml], result.SkippedFiles);
    Assert.Single(result.Warnings);
    Assert.Contains("bytes", result.Warnings[0]);
    Assert.Equal(0, result.FilesSearched);
    Assert.Empty(result.Matches);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/UiPath.Engineering.Mcp.Core.Tests --filter "FullyQualifiedName~SearchText_OversizedFileBySize_SkippedBeforeReading"`
Expected: FAIL — the file is read and searched (1 match found, `FilesSearched == 1`)

- [ ] **Step 3: Rewrite the read loop with the pre-read guard**

In `src/UiPath.Engineering.Mcp.Core/CodeSearch/CodebaseSearchService.cs`, replace lines 39-51 (the `string content;` declaration through the oversized `if`) with:

```csharp
            string content;
            try {
                var fileSize = _filesystem.GetFileSize(file);
                if (fileSize > MaxFileCharacters) {
                    result.SkippedFiles.Add(file);
                    result.Warnings.Add($"Skipped oversized file '{file}' ({fileSize} bytes).");
                    continue;
                }
                content = _filesystem.ReadAllText(file);
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                result.SkippedFiles.Add(file);
                result.Warnings.Add($"Skipped unreadable file '{file}': {ex.Message}");
                continue;
            }
            // Safety net: GetFileSize reports bytes; keep the char-accurate check post-read.
            if (content.Length > MaxFileCharacters) {
                result.SkippedFiles.Add(file);
                result.Warnings.Add($"Skipped oversized file '{file}' ({content.Length} characters).");
                continue;
            }
```

(`continue` inside a `try` with only a `catch` — no `finally` — is legal C#. The redundant `or FileNotFoundException` is gone because `FileNotFoundException` derives from `IOException`.)

Also update the class-level comment on line 15 from `// ~2 MB of text: files larger than this are skipped rather than scanned.` to:

```csharp
    // ~2 MB of text: files larger than this are skipped rather than scanned. The pre-read
    // check compares bytes (GetFileSize) so oversized files are never loaded into memory.
```

- [ ] **Step 4: Run the new test to verify it passes**

Run: `dotnet test tests/UiPath.Engineering.Mcp.Core.Tests --filter "FullyQualifiedName~SearchText_OversizedFileBySize_SkippedBeforeReading"`
Expected: PASS

- [ ] **Step 5: Run the whole SearchText suite (regression: oversized-by-content and unreadable-file tests)**

Run: `dotnet test tests/UiPath.Engineering.Mcp.Core.Tests --filter "FullyQualifiedName~SearchTextTests"`
Expected: all PASS — `SearchText_OversizedFile_SkippedWithWarning` still passes (fake size derives from content length) and `SearchText_UnreadableFile_SkippedWithWarning` confirms the simplified filter still catches `FileNotFoundException`

- [ ] **Step 6: Commit**

```bash
git add src/UiPath.Engineering.Mcp.Core/CodeSearch/CodebaseSearchService.cs tests/UiPath.Engineering.Mcp.Core.Tests/SearchTextTests.cs
git commit -m "fix: check file size before reading in text search; drop redundant FileNotFoundException filter"
```

(Ask the user before committing — see Global Constraints.)

---

### Task 3: Cancellation inside `EnumerateSourceSymbols`

**Files:**
- Modify: `src/UiPath.Engineering.Mcp.Core/CodeSearch/CodebaseSearchService.cs:97` (call site) and `:210-232` (iterator)
- Test: `tests/UiPath.Engineering.Mcp.Core.Tests/SearchSymbolsTests.cs`

**Interfaces:**
- Consumes: existing `CancellationToken cancellationToken` parameter of `SearchSymbolsAsync`; test base `CSharpAnalysisServiceTestBase.BuildContext(source)` and the `SearchSymbolsTests`-local `CreateSearchService(context)` factory and `Source` constant.
- Produces: nothing consumed by later tasks.

- [ ] **Step 1: Write the failing test**

Append to `SearchSymbolsTests`:

```csharp
[Fact]
public async Task SearchSymbols_CancelledToken_ThrowsDuringEnumeration() {
    var sut = CreateSearchService(BuildContext(Source));
    using var cts = new CancellationTokenSource();
    cts.Cancel();

    await Assert.ThrowsAsync<OperationCanceledException>(
        () => sut.SearchSymbolsAsync(Root, "execute", cancellationToken: cts.Token));
}
```

(The stub `ICSharpContextBuilder` in this file ignores the token and returns the context, so the throw can only come from symbol enumeration itself.)

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/UiPath.Engineering.Mcp.Core.Tests --filter "FullyQualifiedName~SearchSymbols_CancelledToken_ThrowsDuringEnumeration"`
Expected: FAIL — no exception thrown, search completes normally

- [ ] **Step 3: Thread the token through the iterator**

In `src/UiPath.Engineering.Mcp.Core/CodeSearch/CodebaseSearchService.cs`:

Change the call site (line 97) from:

```csharp
        var matches = EnumerateSourceSymbols(context.Compilation.GlobalNamespace)
```

to:

```csharp
        var matches = EnumerateSourceSymbols(context.Compilation.GlobalNamespace, cancellationToken)
```

Change the iterator signature (line 210) from:

```csharp
    private static IEnumerable<ISymbol> EnumerateSourceSymbols(INamespaceOrTypeSymbol container) {
        foreach (var member in container.GetMembers()) {
            if (member is INamespaceSymbol ns) {
                foreach (var nested in EnumerateSourceSymbols(ns)) {
```

to:

```csharp
    private static IEnumerable<ISymbol> EnumerateSourceSymbols(INamespaceOrTypeSymbol container, CancellationToken cancellationToken) {
        foreach (var member in container.GetMembers()) {
            cancellationToken.ThrowIfCancellationRequested();
            if (member is INamespaceSymbol ns) {
                foreach (var nested in EnumerateSourceSymbols(ns, cancellationToken)) {
```

and the named-type recursion (line 227) from:

```csharp
                foreach (var nested in EnumerateSourceSymbols(type)) {
```

to:

```csharp
                foreach (var nested in EnumerateSourceSymbols(type, cancellationToken)) {
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/UiPath.Engineering.Mcp.Core.Tests --filter "FullyQualifiedName~SearchSymbols_CancelledToken_ThrowsDuringEnumeration"`
Expected: PASS

- [ ] **Step 5: Run the whole Core test suite**

Run: `dotnet test tests/UiPath.Engineering.Mcp.Core.Tests`
Expected: all PASS

- [ ] **Step 6: Commit**

```bash
git add src/UiPath.Engineering.Mcp.Core/CodeSearch/CodebaseSearchService.cs tests/UiPath.Engineering.Mcp.Core.Tests/SearchSymbolsTests.cs
git commit -m "fix: honor cancellation during source symbol enumeration"
```

(Ask the user before committing — see Global Constraints.)

---

### Final verification

- [ ] Run the full solution test suite: `dotnet test` — all tests PASS.
