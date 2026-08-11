# SP2: Codebase Search Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a `search_codebase` tool with `text` / `symbol` / `activity` / `workflow` modes so Copilot can answer "where is X generated / used" across a UiPath project's `.xaml` and `.cs` files without reading raw files.

**Architecture:** A new `CodeSearch/` folder in `UiPath.Engineering.Mcp.Core` holds `CodebaseSearchService` (one method per mode) plus per-mode DTOs sharing a base envelope. Symbol mode reuses SP1's cached `CSharpCompilation` (`ICSharpContextBuilder`); activity/workflow modes reuse the cached `UiPathProjectModel` (`IProjectModelBuilder`); text mode scans `.xaml` + `.cs` files line-by-line via the existing `IFilesystemProvider` (no interface change). One thin `SearchCodebaseTool` in `UiPath.Engineering.Mcp.Tools` dispatches on `mode`. Deterministic ordering (exact case-sensitive hits first, then case-insensitive, then file path + line); fixed cap of 200 matches with a `truncated` flag.

**Tech Stack:** .NET 8, C# 12, no new dependencies (Roslyn already on Core from SP1), xUnit with hand-written fakes (no Moq).

**Spec:** `docs/superpowers/specs/2026-08-11-codebase-search-design.md`

## Global Constraints

- .NET 8, C# only. No new runtime dependencies.
- Tools never throw raw exceptions to the MCP client; use `ToolResults.Ok` / `ToolResults.Failure` / `ToolResults.FromException` and the standard `ToolResult` envelope.
- All tools guard paths with `ToolResults.GuardProject(_filesystem, projectPath, sw)`.
- DTO properties are PascalCase (matches `UiPathProjectModel` serialization style).
- Symbol-mode `AnalysisMode` values are exactly `"full"`, `"partial"`, `"syntaxOnly"`.
- All modes cap at `CSharpAnalysisService.MaxResults` (200); overflow sets `Truncated = true` plus a note.
- Matching is case-insensitive substring; exact case-sensitive hits order first; then stable by file path + line. No relevance floats.
- Tests: xUnit, hand-written fakes, no Moq. Core tests use the in-memory `FakeFilesystemProvider` (`tests/UiPath.Engineering.Mcp.Core.Tests/FakeFilesystemProvider.cs`).
- Do NOT modify `IFilesystemProvider`, any SP1 `CodeAnalysis/` file, or any existing tool behavior. The only permitted touches to existing files are: `ToolErrorCodes.cs` (add one constant), `Program.cs` (DI), `README.md` (docs).
- Commit after every task; `git add` ONLY the files listed in that task (the working tree has unrelated pending deletions/untracked files — never `git add -A` or `git add .`).
- Test commands run from the repo root: `C:/Users/arauj/Documents/UiPathEngineeringMCP`.

---

### Task 1: DTOs + `ICodebaseSearchService` + text mode

**Files:**
- Create: `src/UiPath.Engineering.Mcp.Core/CodeSearch/CodebaseSearchDtos.cs`
- Create: `src/UiPath.Engineering.Mcp.Core/CodeSearch/ICodebaseSearchService.cs`
- Create: `src/UiPath.Engineering.Mcp.Core/CodeSearch/CodebaseSearchService.cs` (ctor + `SearchTextAsync` only; later tasks append methods)
- Test: `tests/UiPath.Engineering.Mcp.Core.Tests/SearchTextTests.cs`

**Interfaces:**
- Consumes: `IFilesystemProvider` (`FindXamlFiles`, `FindCSharpFiles`, `ReadAllText`), `CSharpAnalysisService.MaxResults` (internal const, 200, same assembly).
- Produces: `ICodebaseSearchService.SearchTextAsync(string projectPath, string query, CancellationToken)` → `TextSearchResult`. DTOs: `CodebaseSearchResult` (`Truncated`, `Note`, `Warnings`), `TextSearchResult` (`Matches`, `FilesSearched`, `SkippedFiles`), `TextMatch` (`FilePath`, `Line`, `Snippet`). Later tasks add `SearchSymbolsAsync` / `SearchActivitiesAsync` / `SearchWorkflowsAsync` to the same interface and service.

- [ ] **Step 1: Write the failing tests**

Create `tests/UiPath.Engineering.Mcp.Core.Tests/SearchTextTests.cs`:

```csharp
using UiPath.Engineering.Mcp.Core.CodeAnalysis;
using UiPath.Engineering.Mcp.Core.CodeSearch;
using UiPath.Engineering.Mcp.Core.Models;
using UiPath.Engineering.Mcp.Core.Parsing;

namespace UiPath.Engineering.Mcp.Core.Tests;

public class SearchTextTests {
    private const string Root = "/projects/testProcess";
    private const string MainXaml = "/projects/testProcess/Main.xaml";
    private const string FlowCs = "/projects/testProcess/InvoiceFlow.cs";

    private sealed class StubContextBuilder : ICSharpContextBuilder {
        public Task<CSharpAnalysisContext> BuildAsync(string projectPath, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class StubProjectModelBuilder : IProjectModelBuilder {
        public Task<UiPathProjectModel> BuildAsync(string projectPath, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private static CodebaseSearchService CreateService(FakeFilesystemProvider fs) =>
        new(new StubContextBuilder(), new StubProjectModelBuilder(), fs);

    private static FakeFilesystemProvider CreateFilesystem() {
        var fs = new FakeFilesystemProvider();
        fs.XamlFiles.Add(MainXaml);
        fs.CSharpFiles.Add(FlowCs);
        fs.FileContents[MainXaml] = "<Sequence DisplayName=\"Dequeue item\"><WriteLine /></Sequence>";
        fs.FileContents[FlowCs] = "public class InvoiceFlow {\n    public object GetQueueItem() { return null; }\n}";
        return fs;
    }

    [Fact]
    public async Task SearchText_CaseInsensitiveSubstring_MatchesAcrossXamlAndCs() {
        var sut = CreateService(CreateFilesystem());

        var result = await sut.SearchTextAsync(Root, "queue");

        Assert.Equal(2, result.FilesSearched);
        Assert.Equal(2, result.Matches.Count);
        Assert.Contains(result.Matches, m => m.FilePath == MainXaml && m.Line == 1);
        Assert.Contains(result.Matches, m => m.FilePath == FlowCs && m.Line == 2);
    }

    [Fact]
    public async Task SearchText_ExactCaseMatches_OrderBeforeCaseInsensitiveOnly() {
        var fs = new FakeFilesystemProvider();
        fs.XamlFiles.Add(MainXaml);
        fs.CSharpFiles.Add(FlowCs);
        // The case-sensitive hit lives in the file that sorts SECOND by path,
        // so only tier-based ordering puts it first.
        fs.FileContents[MainXaml] = "logger.Info(\"starting\")";
        fs.FileContents[FlowCs] = "Log(\"starting\");";
        var sut = CreateService(fs);

        var result = await sut.SearchTextAsync(Root, "Log");

        Assert.Equal(2, result.Matches.Count);
        Assert.Equal(FlowCs, result.Matches[0].FilePath); // exact case-sensitive substring
        Assert.Equal(MainXaml, result.Matches[1].FilePath); // case-insensitive only
    }

    [Fact]
    public async Task SearchText_SnippetTrimmedAndCappedAt300Chars() {
        var fs = new FakeFilesystemProvider();
        fs.XamlFiles.Add(MainXaml);
        fs.FileContents[MainXaml] = "   " + new string('x', 400) + " queue " + new string('y', 100);
        var sut = CreateService(fs);

        var result = await sut.SearchTextAsync(Root, "queue");

        var match = Assert.Single(result.Matches);
        Assert.Equal(301, match.Snippet.Length); // 300 chars + ellipsis
        Assert.EndsWith("…", match.Snippet);
        Assert.False(match.Snippet.StartsWith(' '));
    }

    [Fact]
    public async Task SearchText_OversizedFile_SkippedWithWarning() {
        var fs = new FakeFilesystemProvider();
        fs.XamlFiles.Add(MainXaml);
        fs.CSharpFiles.Add(FlowCs);
        fs.FileContents[MainXaml] = new string('x', 2_000_001); // over the 2 MB guard
        fs.FileContents[FlowCs] = "var queue = 1;";
        var sut = CreateService(fs);

        var result = await sut.SearchTextAsync(Root, "queue");

        Assert.Equal([MainXaml], result.SkippedFiles);
        Assert.Single(result.Warnings);
        Assert.Equal(1, result.FilesSearched);
        Assert.Single(result.Matches);
    }

    [Fact]
    public async Task SearchText_UnreadableFile_SkippedWithWarning() {
        var fs = new FakeFilesystemProvider();
        fs.XamlFiles.Add(MainXaml); // no FileContents entry -> ReadAllText throws FileNotFoundException
        fs.CSharpFiles.Add(FlowCs);
        fs.FileContents[FlowCs] = "var queue = 1;";
        var sut = CreateService(fs);

        var result = await sut.SearchTextAsync(Root, "queue");

        Assert.Equal([MainXaml], result.SkippedFiles);
        Assert.Single(result.Warnings);
        Assert.Single(result.Matches);
    }

    [Fact]
    public async Task SearchText_MoreThan200Matches_Truncated() {
        var fs = new FakeFilesystemProvider();
        fs.XamlFiles.Add(MainXaml);
        fs.FileContents[MainXaml] = string.Join('\n', Enumerable.Repeat("queue", 210));
        var sut = CreateService(fs);

        var result = await sut.SearchTextAsync(Root, "queue");

        Assert.Equal(200, result.Matches.Count);
        Assert.True(result.Truncated);
        Assert.Contains("truncated", result.Note);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/UiPath.Engineering.Mcp.Core.Tests --filter "FullyQualifiedName~SearchTextTests"`
Expected: FAIL — type `CodebaseSearchService` does not exist (compile error).

- [ ] **Step 3: Implement the DTOs, interface, and text mode**

Create `src/UiPath.Engineering.Mcp.Core/CodeSearch/CodebaseSearchDtos.cs`:

```csharp
using UiPath.Engineering.Mcp.Core.CodeAnalysis;

namespace UiPath.Engineering.Mcp.Core.CodeSearch;

/// <summary>
/// Base shape every codebase-search response carries: truncation signal,
/// human-readable caveats, and non-fatal warnings.
/// </summary>
public abstract class CodebaseSearchResult {
    public bool Truncated { get; set; }
    public string? Note { get; set; }
    public List<string> Warnings { get; set; } = [];
}

public sealed class TextMatch {
    public string FilePath { get; init; } = string.Empty;
    public int Line { get; init; }
    public string Snippet { get; init; } = string.Empty;
}

public sealed class TextSearchResult : CodebaseSearchResult {
    public List<TextMatch> Matches { get; init; } = [];
    public int FilesSearched { get; set; }
    public List<string> SkippedFiles { get; init; } = [];
}

public sealed class SymbolSearchResult : CodebaseSearchResult {
    public List<SymbolMatch> Matches { get; init; } = [];
    public string AnalysisMode { get; set; } = "full";
    public List<string> UnresolvedReferences { get; set; } = [];
    public bool HasCSharpFiles { get; set; } = true;
}

public sealed class ActivityMatch {
    public string WorkflowFile { get; init; } = string.Empty;
    public string WorkflowPath { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string ActivityType { get; init; } = string.Empty;
    public int Depth { get; init; }
}

public sealed class ActivitySearchResult : CodebaseSearchResult {
    public List<ActivityMatch> Matches { get; init; } = [];
    public int WorkflowsSearched { get; set; }
}

public sealed class WorkflowMatch {
    public string FileName { get; init; } = string.Empty;
    public string FilePath { get; init; } = string.Empty;
    public bool IsMain { get; init; }
    public string? Description { get; init; }
    public string MatchedOn { get; init; } = string.Empty; // name | description | both
}

public sealed class WorkflowSearchResult : CodebaseSearchResult {
    public List<WorkflowMatch> Matches { get; init; } = [];
}
```

Create `src/UiPath.Engineering.Mcp.Core/CodeSearch/ICodebaseSearchService.cs`:

```csharp
namespace UiPath.Engineering.Mcp.Core.CodeSearch;

public interface ICodebaseSearchService {
    Task<TextSearchResult> SearchTextAsync(string projectPath, string query, CancellationToken cancellationToken = default);
}
```

Create `src/UiPath.Engineering.Mcp.Core/CodeSearch/CodebaseSearchService.cs`:

```csharp
using UiPath.Engineering.Mcp.Core.Abstractions;
using UiPath.Engineering.Mcp.Core.CodeAnalysis;
using UiPath.Engineering.Mcp.Core.Parsing;

namespace UiPath.Engineering.Mcp.Core.CodeSearch;

/// <summary>
/// Substring search over a UiPath project's .xaml and .cs files. Text mode scans
/// file contents line-by-line; symbol mode reuses SP1's cached compilation;
/// activity/workflow modes reuse the cached UiPathProjectModel. Stateless beyond
/// the injected caches; every method takes the project path.
/// </summary>
public sealed class CodebaseSearchService : ICodebaseSearchService {
    // ~2 MB of text: files larger than this are skipped rather than scanned.
    internal const int MaxFileCharacters = 2_000_000;
    private const int MaxSnippetLength = 300;

    private readonly ICSharpContextBuilder _contextBuilder;
    private readonly IProjectModelBuilder _projectModelBuilder;
    private readonly IFilesystemProvider _filesystem;

    public CodebaseSearchService(
        ICSharpContextBuilder contextBuilder,
        IProjectModelBuilder projectModelBuilder,
        IFilesystemProvider filesystem) {
        _contextBuilder = contextBuilder;
        _projectModelBuilder = projectModelBuilder;
        _filesystem = filesystem;
    }

    public Task<TextSearchResult> SearchTextAsync(string projectPath, string query, CancellationToken cancellationToken = default) {
        var result = new TextSearchResult();
        var matches = new List<(TextMatch Match, bool Exact)>();

        var files = _filesystem.FindXamlFiles(projectPath).Concat(_filesystem.FindCSharpFiles(projectPath));
        foreach (var file in files) {
            cancellationToken.ThrowIfCancellationRequested();
            string content;
            try {
                content = _filesystem.ReadAllText(file);
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FileNotFoundException) {
                result.SkippedFiles.Add(file);
                result.Warnings.Add($"Skipped unreadable file '{file}': {ex.Message}");
                continue;
            }
            if (content.Length > MaxFileCharacters) {
                result.SkippedFiles.Add(file);
                result.Warnings.Add($"Skipped oversized file '{file}' ({content.Length} characters).");
                continue;
            }
            result.FilesSearched++;

            var lines = content.Split('\n');
            for (var i = 0; i < lines.Length; i++) {
                var exact = lines[i].Contains(query, StringComparison.Ordinal);
                if (!exact && !lines[i].Contains(query, StringComparison.OrdinalIgnoreCase)) {
                    continue;
                }
                matches.Add((new TextMatch { FilePath = file, Line = i + 1, Snippet = TrimSnippet(lines[i]) }, exact));
            }
        }

        foreach (var (match, _) in matches
            .OrderBy(m => m.Exact ? 0 : 1)
            .ThenBy(m => m.Match.FilePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(m => m.Match.Line)
            .Take(CSharpAnalysisService.MaxResults)) {
            result.Matches.Add(match);
        }
        if (matches.Count > CSharpAnalysisService.MaxResults) {
            result.Truncated = true;
            result.Note = $"Results truncated at {CSharpAnalysisService.MaxResults} matches; narrow the query.";
        }
        return Task.FromResult(result);
    }

    private static string TrimSnippet(string line) {
        var trimmed = line.Trim();
        return trimmed.Length <= MaxSnippetLength ? trimmed : trimmed[..MaxSnippetLength] + "…";
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/UiPath.Engineering.Mcp.Core.Tests --filter "FullyQualifiedName~SearchTextTests"`
Expected: PASS (6 tests).

- [ ] **Step 5: Commit**

```bash
git add src/UiPath.Engineering.Mcp.Core/CodeSearch/CodebaseSearchDtos.cs src/UiPath.Engineering.Mcp.Core/CodeSearch/ICodebaseSearchService.cs src/UiPath.Engineering.Mcp.Core/CodeSearch/CodebaseSearchService.cs tests/UiPath.Engineering.Mcp.Core.Tests/SearchTextTests.cs
git commit -m "feat: add CodebaseSearchService text mode (line scan over .xaml/.cs)"
```

---

### Task 2: Symbol mode (`SearchSymbolsAsync`)

**Files:**
- Modify: `src/UiPath.Engineering.Mcp.Core/CodeSearch/ICodebaseSearchService.cs` (add `SearchSymbolsAsync`)
- Modify: `src/UiPath.Engineering.Mcp.Core/CodeSearch/CodebaseSearchService.cs` (append `SearchSymbolsAsync` + private enumerator)
- Test: `tests/UiPath.Engineering.Mcp.Core.Tests/SearchSymbolsTests.cs`

**Interfaces:**
- Consumes: `ICSharpContextBuilder.BuildAsync` → `CSharpAnalysisContext` (`Compilation`, `Mode`, `UnresolvedReferences`, `Warnings`, `HasCSharpFiles`); SP1 internals `CSharpAnalysisService.KindMatches(ISymbol, string?)`, `CSharpAnalysisService.ToSymbolMatch(ISymbol)`, `CSharpAnalysisService.MaxResults`.
- Produces: `ICodebaseSearchService.SearchSymbolsAsync(string projectPath, string query, string? kind = null, CancellationToken)` → `SymbolSearchResult` (DTO from Task 1). Consumed by the tool in Task 4.

- [ ] **Step 1: Write the failing tests**

Create `tests/UiPath.Engineering.Mcp.Core.Tests/SearchSymbolsTests.cs`:

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using UiPath.Engineering.Mcp.Core.CodeAnalysis;
using UiPath.Engineering.Mcp.Core.CodeSearch;
using UiPath.Engineering.Mcp.Core.Models;
using UiPath.Engineering.Mcp.Core.Parsing;

namespace UiPath.Engineering.Mcp.Core.Tests;

public class SearchSymbolsTests : CSharpAnalysisServiceTestBase {
    private const string Source = """
        namespace TestProcess;

        public class InvoiceFlow {
            public string QueueName { get; set; }
            public int Execute(string input) { return 1; }
            public int ExecuteAsync(string input) { return 2; }
            public void Log(string message) { }
            public void LogMessage(string message) { }
        }
        """;

    private sealed class StubContextBuilder(CSharpAnalysisContext context) : ICSharpContextBuilder {
        public Task<CSharpAnalysisContext> BuildAsync(string projectPath, CancellationToken cancellationToken = default) =>
            Task.FromResult(context);
    }

    private sealed class StubProjectModelBuilder : IProjectModelBuilder {
        public Task<UiPathProjectModel> BuildAsync(string projectPath, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    // Named CreateSearchService (not CreateService) to avoid hiding the base
    // class's CSharpAnalysisService factory.
    private static CodebaseSearchService CreateSearchService(CSharpAnalysisContext context) =>
        new(new StubContextBuilder(context), new StubProjectModelBuilder(), new FakeFilesystemProvider());

    [Fact]
    public async Task SearchSymbols_SubstringMatch_FindsMethodsCaseInsensitively() {
        var sut = CreateSearchService(BuildContext(Source));

        var result = await sut.SearchSymbolsAsync(Root, "execute");

        Assert.Equal(2, result.Matches.Count);
        Assert.All(result.Matches, m => Assert.Equal("method", m.Kind));
        Assert.All(result.Matches, m => Assert.Equal(FlowCs, m.FilePath));
        Assert.Equal("full", result.AnalysisMode);
    }

    [Fact]
    public async Task SearchSymbols_KindFilter_NarrowsMatches() {
        var sut = CreateSearchService(BuildContext(Source));

        var methods = await sut.SearchSymbolsAsync(Root, "invoice", kind: "class");
        var properties = await sut.SearchSymbolsAsync(Root, "queue", kind: "property");
        var wrongKind = await sut.SearchSymbolsAsync(Root, "queue", kind: "method");

        var type = Assert.Single(methods.Matches);
        Assert.Equal("class", type.Kind);
        Assert.Equal("InvoiceFlow", type.Name);
        var property = Assert.Single(properties.Matches);
        Assert.Equal("QueueName", property.Name);
        Assert.Empty(wrongKind.Matches);
    }

    [Fact]
    public async Task SearchSymbols_ExactNameMatch_OrdersFirst() {
        var sut = CreateSearchService(BuildContext(Source));

        var result = await sut.SearchSymbolsAsync(Root, "Log");

        Assert.Equal(2, result.Matches.Count);
        Assert.Equal("Log", result.Matches[0].Name); // exact ordinal-name equality
        Assert.Equal("LogMessage", result.Matches[1].Name);
    }

    [Fact]
    public async Task SearchSymbols_PartialMode_CarriesTransparencyFields() {
        var context = BuildContext(Source, mode: CSharpAnalysisMode.Partial, unresolved: ["UiPath.System.Activities"]);
        var sut = CreateSearchService(context);

        var result = await sut.SearchSymbolsAsync(Root, "Execute");

        Assert.Equal("partial", result.AnalysisMode);
        Assert.Equal(["UiPath.System.Activities"], result.UnresolvedReferences);
        Assert.NotEmpty(result.Matches); // source symbols still resolve in degraded modes
    }

    [Fact]
    public async Task SearchSymbols_NoCSharpFiles_NotesAndReturnsEmpty() {
        var context = new CSharpAnalysisContext {
            Compilation = CSharpCompilation.Create(
                "analysis-empty",
                options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)),
            Mode = CSharpAnalysisMode.Full,
            HasCSharpFiles = false
        };
        var sut = CreateSearchService(context);

        var result = await sut.SearchSymbolsAsync(Root, "Execute");

        Assert.False(result.HasCSharpFiles);
        Assert.Empty(result.Matches);
        Assert.Equal("The project contains no C# files.", result.Note);
    }

    [Fact]
    public async Task SearchSymbols_MoreThan200Matches_Truncated() {
        var members = string.Join("\n", Enumerable.Range(0, 210).Select(i => $"    public void Log{i}() {{ }}"));
        var source = $"public class Bulk {{\n{members}\n}}";
        var sut = CreateSearchService(BuildContext(source));

        var result = await sut.SearchSymbolsAsync(Root, "Log");

        Assert.Equal(200, result.Matches.Count);
        Assert.True(result.Truncated);
        Assert.Contains("truncated", result.Note);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/UiPath.Engineering.Mcp.Core.Tests --filter "FullyQualifiedName~SearchSymbolsTests"`
Expected: FAIL — `ICodebaseSearchService` does not contain `SearchSymbolsAsync` (compile error).

- [ ] **Step 3: Implement symbol mode**

In `src/UiPath.Engineering.Mcp.Core/CodeSearch/ICodebaseSearchService.cs`, add to the interface:

```csharp
Task<SymbolSearchResult> SearchSymbolsAsync(string projectPath, string query, string? kind = null, CancellationToken cancellationToken = default);
```

In `src/UiPath.Engineering.Mcp.Core/CodeSearch/CodebaseSearchService.cs`, add `using Microsoft.CodeAnalysis;` at the top and append to the class:

```csharp
public async Task<SymbolSearchResult> SearchSymbolsAsync(string projectPath, string query, string? kind = null, CancellationToken cancellationToken = default) {
    var context = await _contextBuilder.BuildAsync(projectPath, cancellationToken);
    var result = new SymbolSearchResult {
        AnalysisMode = context.Mode switch {
            CSharpAnalysisMode.Full => "full",
            CSharpAnalysisMode.Partial => "partial",
            _ => "syntaxOnly"
        },
        UnresolvedReferences = [.. context.UnresolvedReferences],
        Warnings = [.. context.Warnings],
        HasCSharpFiles = context.HasCSharpFiles
    };
    if (!context.HasCSharpFiles) {
        result.Note = "The project contains no C# files.";
        return result;
    }

    // GetSymbolsWithName only does exact-name lookup, so substring search
    // enumerates source symbols from the global namespace instead.
    var matches = EnumerateSourceSymbols(context.Compilation.GlobalNamespace)
        .Where(s => s.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
        .Where(s => CSharpAnalysisService.KindMatches(s, kind))
        .Select(s => (Match: CSharpAnalysisService.ToSymbolMatch(s), Exact: string.Equals(s.Name, query, StringComparison.Ordinal)))
        .OrderBy(m => m.Exact ? 0 : 1)
        .ThenBy(m => m.Match.FilePath, StringComparer.OrdinalIgnoreCase)
        .ThenBy(m => m.Match.Line)
        .ThenBy(m => m.Match.Name, StringComparer.Ordinal)
        .ToList();

    foreach (var (match, _) in matches.Take(CSharpAnalysisService.MaxResults)) {
        result.Matches.Add(match);
    }
    if (matches.Count > CSharpAnalysisService.MaxResults) {
        result.Truncated = true;
        result.Note = $"Results truncated at {CSharpAnalysisService.MaxResults} matches; narrow the query.";
    }
    return result;
}

// Yields source-declared named types (recursing into their members), methods,
// properties, and fields. Metadata symbols and implicit members are excluded.
private static IEnumerable<ISymbol> EnumerateSourceSymbols(INamespaceOrTypeSymbol container) {
    foreach (var member in container.GetMembers()) {
        if (member is INamespaceSymbol ns) {
            foreach (var nested in EnumerateSourceSymbols(ns)) {
                yield return nested;
            }
            continue;
        }

        if (member.IsImplicitlyDeclared || !member.Locations.Any(l => l.IsInSource)) {
            continue;
        }
        if (member is INamedTypeSymbol or IMethodSymbol or IPropertySymbol or IFieldSymbol) {
            yield return member;
        }
        if (member is INamedTypeSymbol type) {
            foreach (var nested in EnumerateSourceSymbols(type)) {
                yield return nested;
            }
        }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/UiPath.Engineering.Mcp.Core.Tests --filter "FullyQualifiedName~SearchSymbolsTests"`
Expected: PASS (6 tests).

- [ ] **Step 5: Commit**

```bash
git add src/UiPath.Engineering.Mcp.Core/CodeSearch/ICodebaseSearchService.cs src/UiPath.Engineering.Mcp.Core/CodeSearch/CodebaseSearchService.cs tests/UiPath.Engineering.Mcp.Core.Tests/SearchSymbolsTests.cs
git commit -m "feat: add CodebaseSearchService symbol mode over cached Roslyn compilation"
```

---

### Task 3: Activity + workflow modes

**Files:**
- Modify: `src/UiPath.Engineering.Mcp.Core/CodeSearch/ICodebaseSearchService.cs` (add `SearchActivitiesAsync`, `SearchWorkflowsAsync`)
- Modify: `src/UiPath.Engineering.Mcp.Core/CodeSearch/CodebaseSearchService.cs` (append both methods)
- Test: `tests/UiPath.Engineering.Mcp.Core.Tests/SearchActivitiesWorkflowsTests.cs`

**Interfaces:**
- Consumes: `IProjectModelBuilder.BuildAsync` → `UiPathProjectModel` (`Workflows` → `WorkflowModel` with `FileName`, `FilePath`, `IsMain`, `Description`, `HasParseError`, `Activities` → `ActivityModel` with `DisplayName`, `Type`, `Depth`); `CSharpAnalysisService.MaxResults`.
- Produces: `ICodebaseSearchService.SearchActivitiesAsync(string projectPath, string query, CancellationToken)` → `ActivitySearchResult`; `ICodebaseSearchService.SearchWorkflowsAsync(string projectPath, string query, CancellationToken)` → `WorkflowSearchResult` (DTOs from Task 1). Consumed by the tool in Task 4.

- [ ] **Step 1: Write the failing tests**

Create `tests/UiPath.Engineering.Mcp.Core.Tests/SearchActivitiesWorkflowsTests.cs`:

```csharp
using UiPath.Engineering.Mcp.Core.CodeAnalysis;
using UiPath.Engineering.Mcp.Core.CodeSearch;
using UiPath.Engineering.Mcp.Core.Models;
using UiPath.Engineering.Mcp.Core.Parsing;

namespace UiPath.Engineering.Mcp.Core.Tests;

public class SearchActivitiesWorkflowsTests {
    private const string Root = "/projects/testProcess";

    private sealed class StubContextBuilder : ICSharpContextBuilder {
        public Task<CSharpAnalysisContext> BuildAsync(string projectPath, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeProjectModelBuilder(UiPathProjectModel model) : IProjectModelBuilder {
        public Task<UiPathProjectModel> BuildAsync(string projectPath, CancellationToken cancellationToken = default) =>
            Task.FromResult(model);
    }

    private static CodebaseSearchService CreateService(UiPathProjectModel model) =>
        new(new StubContextBuilder(), new FakeProjectModelBuilder(model), new FakeFilesystemProvider());

    private static UiPathProjectModel BuildModel() => new() {
        ProjectName = "testProcess",
        Workflows = [
            new WorkflowModel {
                FileName = "Main.xaml",
                FilePath = "/projects/testProcess/Main.xaml",
                IsMain = true,
                Description = "Entry point for invoice processing",
                Activities = [
                    new ActivityModel { DisplayName = "Log start", Type = "LogMessage", Depth = 1 },
                    new ActivityModel { DisplayName = "Log", Type = "LogMessage", Depth = 1 },
                    new ActivityModel { DisplayName = "Write line", Type = "WriteLine", Depth = 2 }
                ]
            },
            new WorkflowModel {
                FileName = "InvoiceFlow.xaml",
                FilePath = "/projects/testProcess/InvoiceFlow.xaml",
                Activities = [
                    new ActivityModel { DisplayName = "Log invoice", Type = "LogMessage", Depth = 1 }
                ]
            },
            new WorkflowModel {
                FileName = "Broken.xaml",
                FilePath = "/projects/testProcess/Broken.xaml",
                HasParseError = true,
                ParseError = "XAML parse failure: boom"
            }
        ]
    };

    // --- activity mode ---

    [Fact]
    public async Task SearchActivities_MatchesDisplayNameAndTypeAcrossWorkflows() {
        var sut = CreateService(BuildModel());

        var result = await sut.SearchActivitiesAsync(Root, "log");

        Assert.Equal(3, result.Matches.Count);
        Assert.All(result.Matches, m => Assert.Equal("LogMessage", m.ActivityType));
        Assert.Contains(result.Matches, m => m.WorkflowFile == "InvoiceFlow.xaml" && m.DisplayName == "Log invoice");
        Assert.Equal(2, result.WorkflowsSearched); // Broken.xaml skipped
        Assert.Contains("1 workflow(s) failed to parse", result.Note);
        Assert.Contains("line-level activity addressing", result.Note); // SP3 limitation note
    }

    [Fact]
    public async Task SearchActivities_ExactNameMatch_OrdersBeforeCaseInsensitiveOnly() {
        var sut = CreateService(BuildModel());

        var result = await sut.SearchActivitiesAsync(Root, "Log");

        Assert.Equal(3, result.Matches.Count);
        Assert.Equal("Log", result.Matches[0].DisplayName); // exact ordinal-name equality
    }

    [Fact]
    public async Task SearchActivities_TypeOnlyMatch_PassesDepthThrough() {
        var sut = CreateService(BuildModel());

        var result = await sut.SearchActivitiesAsync(Root, "WriteLine");

        var match = Assert.Single(result.Matches);
        Assert.Equal("Write line", match.DisplayName);
        Assert.Equal(2, match.Depth);
        Assert.Equal("/projects/testProcess/Main.xaml", match.WorkflowPath);
    }

    // --- workflow mode ---

    [Fact]
    public async Task SearchWorkflows_MatchesFileNameAndDescription() {
        var sut = CreateService(BuildModel());

        var result = await sut.SearchWorkflowsAsync(Root, "invoice");

        Assert.Equal(2, result.Matches.Count);
        var byName = Assert.Single(result.Matches, m => m.FileName == "InvoiceFlow.xaml");
        Assert.Equal("name", byName.MatchedOn);
        var byDescription = Assert.Single(result.Matches, m => m.FileName == "Main.xaml");
        Assert.Equal("description", byDescription.MatchedOn);
        Assert.True(byDescription.IsMain);
    }

    [Fact]
    public async Task SearchWorkflows_NameAndDescriptionHit_MatchedOnBoth() {
        var model = new UiPathProjectModel {
            ProjectName = "testProcess",
            Workflows = [
                new WorkflowModel {
                    FileName = "Invoice.xaml",
                    FilePath = "/projects/testProcess/Invoice.xaml",
                    Description = "Handles invoice retries"
                }
            ]
        };
        var sut = CreateService(model);

        var result = await sut.SearchWorkflowsAsync(Root, "invoice");

        var match = Assert.Single(result.Matches);
        Assert.Equal("both", match.MatchedOn);
    }

    [Fact]
    public async Task SearchWorkflows_ParseErrorWorkflow_StillNameMatchableWithNote() {
        var sut = CreateService(BuildModel());

        var result = await sut.SearchWorkflowsAsync(Root, "broken");

        var match = Assert.Single(result.Matches);
        Assert.Equal("Broken.xaml", match.FileName);
        Assert.Equal("name", match.MatchedOn);
        Assert.Contains("1 workflow(s) failed to parse", result.Note);
    }

    [Fact]
    public async Task SearchWorkflows_ExactNameMatch_OrdersFirst() {
        var model = new UiPathProjectModel {
            ProjectName = "testProcess",
            Workflows = [
                new WorkflowModel { FileName = "ALogin.xaml", FilePath = "/projects/testProcess/ALogin.xaml" },
                new WorkflowModel { FileName = "Log.xaml", FilePath = "/projects/testProcess/Log.xaml" }
            ]
        };
        var sut = CreateService(model);

        var result = await sut.SearchWorkflowsAsync(Root, "Log");

        Assert.Equal(2, result.Matches.Count);
        Assert.Equal("Log.xaml", result.Matches[0].FileName); // exact ordinal-name equality
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/UiPath.Engineering.Mcp.Core.Tests --filter "FullyQualifiedName~SearchActivitiesWorkflowsTests"`
Expected: FAIL — `ICodebaseSearchService` does not contain `SearchActivitiesAsync` (compile error).

- [ ] **Step 3: Implement activity and workflow modes**

In `src/UiPath.Engineering.Mcp.Core/CodeSearch/ICodebaseSearchService.cs`, add to the interface:

```csharp
Task<ActivitySearchResult> SearchActivitiesAsync(string projectPath, string query, CancellationToken cancellationToken = default);
Task<WorkflowSearchResult> SearchWorkflowsAsync(string projectPath, string query, CancellationToken cancellationToken = default);
```

In `src/UiPath.Engineering.Mcp.Core/CodeSearch/CodebaseSearchService.cs`, append to the class:

```csharp
public async Task<ActivitySearchResult> SearchActivitiesAsync(string projectPath, string query, CancellationToken cancellationToken = default) {
    var model = await _projectModelBuilder.BuildAsync(projectPath, cancellationToken);
    var result = new ActivitySearchResult();
    var parseErrors = model.Workflows.Count(w => w.HasParseError);
    var matches = new List<(ActivityMatch Match, bool Exact)>();

    foreach (var workflow in model.Workflows.Where(w => !w.HasParseError)) {
        result.WorkflowsSearched++;
        foreach (var activity in workflow.Activities) {
            cancellationToken.ThrowIfCancellationRequested();
            var nameHit = activity.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase);
            var typeHit = activity.Type.Contains(query, StringComparison.OrdinalIgnoreCase);
            if (!nameHit && !typeHit) {
                continue;
            }
            var exact = string.Equals(activity.DisplayName, query, StringComparison.Ordinal)
                || string.Equals(activity.Type, query, StringComparison.Ordinal);
            matches.Add((new ActivityMatch {
                WorkflowFile = workflow.FileName,
                WorkflowPath = workflow.FilePath,
                DisplayName = activity.DisplayName,
                ActivityType = activity.Type,
                Depth = activity.Depth
            }, exact));
        }
    }

    // OrderBy is stable: within the same workflow, activities keep document order.
    foreach (var (match, _) in matches
        .OrderBy(m => m.Exact ? 0 : 1)
        .ThenBy(m => m.Match.WorkflowPath, StringComparer.OrdinalIgnoreCase)
        .Take(CSharpAnalysisService.MaxResults)) {
        result.Matches.Add(match);
    }

    var notes = new List<string>();
    if (parseErrors > 0) {
        notes.Add($"{parseErrors} workflow(s) failed to parse and were skipped.");
    }
    if (result.Matches.Count > 0) {
        notes.Add("Activity hits locate the workflow file; line-level activity addressing lands with SP3.");
    }
    if (matches.Count > CSharpAnalysisService.MaxResults) {
        result.Truncated = true;
        notes.Add($"Results truncated at {CSharpAnalysisService.MaxResults} matches; narrow the query.");
    }
    result.Note = notes.Count > 0 ? string.Join(' ', notes) : null;
    return result;
}

public async Task<WorkflowSearchResult> SearchWorkflowsAsync(string projectPath, string query, CancellationToken cancellationToken = default) {
    var model = await _projectModelBuilder.BuildAsync(projectPath, cancellationToken);
    var result = new WorkflowSearchResult();

    var matches = model.Workflows
        .Select(w => {
            var nameHit = w.FileName.Contains(query, StringComparison.OrdinalIgnoreCase);
            var descriptionHit = w.Description?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false;
            return (Workflow: w, NameHit: nameHit, DescriptionHit: descriptionHit);
        })
        .Where(x => x.NameHit || x.DescriptionHit)
        .Select(x => (Match: new WorkflowMatch {
            FileName = x.Workflow.FileName,
            FilePath = x.Workflow.FilePath,
            IsMain = x.Workflow.IsMain,
            Description = x.Workflow.Description,
            MatchedOn = x.NameHit && x.DescriptionHit ? "both" : x.NameHit ? "name" : "description"
        }, Exact: string.Equals(x.Workflow.FileName, query, StringComparison.Ordinal)))
        .OrderBy(x => x.Exact ? 0 : 1)
        .ThenBy(x => x.Match.FilePath, StringComparer.OrdinalIgnoreCase)
        .ToList();

    foreach (var (match, _) in matches.Take(CSharpAnalysisService.MaxResults)) {
        result.Matches.Add(match);
    }

    var notes = new List<string>();
    var parseErrors = model.Workflows.Count(w => w.HasParseError);
    if (parseErrors > 0) {
        notes.Add($"{parseErrors} workflow(s) failed to parse.");
    }
    if (matches.Count > CSharpAnalysisService.MaxResults) {
        result.Truncated = true;
        notes.Add($"Results truncated at {CSharpAnalysisService.MaxResults} matches; narrow the query.");
    }
    result.Note = notes.Count > 0 ? string.Join(' ', notes) : null;
    return result;
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/UiPath.Engineering.Mcp.Core.Tests --filter "FullyQualifiedName~SearchActivitiesWorkflowsTests"`
Expected: PASS (7 tests).

- [ ] **Step 5: Commit**

```bash
git add src/UiPath.Engineering.Mcp.Core/CodeSearch/ICodebaseSearchService.cs src/UiPath.Engineering.Mcp.Core/CodeSearch/CodebaseSearchService.cs tests/UiPath.Engineering.Mcp.Core.Tests/SearchActivitiesWorkflowsTests.cs
git commit -m "feat: add CodebaseSearchService activity and workflow modes over cached project model"
```

---

### Task 4: `SearchCodebaseTool` + `INVALID_ARGUMENT` error code

**Files:**
- Modify: `src/UiPath.Engineering.Mcp.Core/ToolErrorCodes.cs` (add one constant)
- Create: `src/UiPath.Engineering.Mcp.Tools/SearchCodebaseTool.cs`
- Modify: `tests/UiPath.Engineering.Mcp.Tools.Tests/Fakes.cs` (append `FakeCodebaseSearchService`)
- Test: `tests/UiPath.Engineering.Mcp.Tools.Tests/SearchCodebaseToolTests.cs`

**Interfaces:**
- Consumes: `ICodebaseSearchService` (all four methods, Tasks 1-3), `ToolResults.GuardProject` / `Ok` / `Failure` / `FromException`, `ToolError` record (`ErrorCode`, `Message`, `FixHint`).
- Produces: `ToolErrorCodes.InvalidArgument` = `"INVALID_ARGUMENT"`; MCP tool `search_codebase(projectPath, query, mode, kind?)` → `ToolResult` whose `Data` is one of the Task 1 DTOs. Registered in DI in Task 5.

- [ ] **Step 1: Write the failing tests**

Create `tests/UiPath.Engineering.Mcp.Tools.Tests/SearchCodebaseToolTests.cs`:

```csharp
using UiPath.Engineering.Mcp.Core;
using UiPath.Engineering.Mcp.Core.CodeAnalysis;
using UiPath.Engineering.Mcp.Core.CodeSearch;

namespace UiPath.Engineering.Mcp.Tools.Tests;

public class SearchCodebaseToolTests {
    private static FakeFilesystemProvider ProjectFilesystem() =>
        new() { Allowed = true, ProjectJson = "/projects/testProcess/project.json" };

    [Fact]
    public async Task SearchCodebase_PathNotAllowed_ReturnsError() {
        var tool = new SearchCodebaseTool(new FakeFilesystemProvider { Allowed = false }, new FakeCodebaseSearchService());

        var result = await tool.SearchCodebase("/not/allowed", "queue", "text");

        Assert.Equal("error", result.Status);
        Assert.Equal("Path not allowed.", result.Summary);
    }

    [Fact]
    public async Task SearchCodebase_ProjectJsonMissing_ReturnsError() {
        var tool = new SearchCodebaseTool(new FakeFilesystemProvider { Allowed = true, ProjectJson = null }, new FakeCodebaseSearchService());

        var result = await tool.SearchCodebase("/projects/testProcess", "queue", "text");

        Assert.Equal("error", result.Status);
        Assert.Equal("project.json not found.", result.Summary);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task SearchCodebase_BlankQuery_ReturnsInvalidArgument(string query) {
        var tool = new SearchCodebaseTool(ProjectFilesystem(), new FakeCodebaseSearchService());

        var result = await tool.SearchCodebase("/projects/testProcess", query, "text");

        Assert.Equal("error", result.Status);
        Assert.Contains(result.ErrorDetails!, e => e.ErrorCode == ToolErrorCodes.InvalidArgument);
    }

    [Fact]
    public async Task SearchCodebase_UnknownMode_ReturnsInvalidArgumentListingModes() {
        var tool = new SearchCodebaseTool(ProjectFilesystem(), new FakeCodebaseSearchService());

        var result = await tool.SearchCodebase("/projects/testProcess", "queue", "semantic");

        Assert.Equal("error", result.Status);
        var error = Assert.Single(result.ErrorDetails!, e => e.ErrorCode == ToolErrorCodes.InvalidArgument);
        Assert.Contains("text", error.Message);
        Assert.Contains("symbol", error.Message);
        Assert.Contains("activity", error.Message);
        Assert.Contains("workflow", error.Message);
    }

    [Fact]
    public async Task SearchCodebase_TextMode_DispatchesAndSummarizes() {
        var search = new FakeCodebaseSearchService {
            TextResult = new TextSearchResult {
                Matches = [new TextMatch { FilePath = "Main.xaml", Line = 3, Snippet = "queue" }],
                FilesSearched = 2
            }
        };
        var tool = new SearchCodebaseTool(ProjectFilesystem(), search);

        var result = await tool.SearchCodebase("/projects/testProcess", "queue", "text");

        Assert.Equal("success", result.Status);
        Assert.Equal("/projects/testProcess", search.LastProjectPath);
        Assert.Equal("queue", search.LastQuery);
        Assert.Contains("1 text match(es)", result.Summary);
        Assert.IsType<TextSearchResult>(result.Data);
    }

    [Fact]
    public async Task SearchCodebase_SymbolMode_ForwardsKindCaseInsensitively() {
        var search = new FakeCodebaseSearchService {
            SymbolResult = new SymbolSearchResult {
                Matches = [new SymbolMatch { Name = "Execute", Kind = "method", FilePath = "Flow.cs", Line = 6 }]
            }
        };
        var tool = new SearchCodebaseTool(ProjectFilesystem(), search);

        var result = await tool.SearchCodebase("/projects/testProcess", "Execute", "Symbol", kind: "method");

        Assert.Equal("success", result.Status);
        Assert.Equal("method", search.LastKind);
        Assert.IsType<SymbolSearchResult>(result.Data);
    }

    [Fact]
    public async Task SearchCodebase_ActivityMode_Dispatches() {
        var search = new FakeCodebaseSearchService {
            ActivityResult = new ActivitySearchResult {
                Matches = [new ActivityMatch { WorkflowFile = "Main.xaml", DisplayName = "Log start", ActivityType = "LogMessage", Depth = 1 }],
                WorkflowsSearched = 2
            }
        };
        var tool = new SearchCodebaseTool(ProjectFilesystem(), search);

        var result = await tool.SearchCodebase("/projects/testProcess", "log", "activity");

        Assert.Equal("success", result.Status);
        Assert.IsType<ActivitySearchResult>(result.Data);
    }

    [Fact]
    public async Task SearchCodebase_WorkflowMode_Dispatches() {
        var search = new FakeCodebaseSearchService {
            WorkflowResult = new WorkflowSearchResult {
                Matches = [new WorkflowMatch { FileName = "Main.xaml", FilePath = "/p/Main.xaml", IsMain = true, MatchedOn = "name" }]
            }
        };
        var tool = new SearchCodebaseTool(ProjectFilesystem(), search);

        var result = await tool.SearchCodebase("/projects/testProcess", "main", "workflow");

        Assert.Equal("success", result.Status);
        Assert.IsType<WorkflowSearchResult>(result.Data);
    }

    [Fact]
    public async Task SearchCodebase_ServiceThrows_ReturnsStructuredError() {
        var search = new FakeCodebaseSearchService { ToThrow = new InvalidOperationException("boom") };
        var tool = new SearchCodebaseTool(ProjectFilesystem(), search);

        var result = await tool.SearchCodebase("/projects/testProcess", "queue", "text");

        Assert.Equal("error", result.Status);
        Assert.Contains("boom", result.Errors);
    }
}
```

Append to `tests/UiPath.Engineering.Mcp.Tools.Tests/Fakes.cs` (after `FakeCSharpAnalysisService`; add `using UiPath.Engineering.Mcp.Core.CodeSearch;` at the top):

```csharp
internal sealed class FakeCodebaseSearchService : ICodebaseSearchService {
    public TextSearchResult TextResult { get; set; } = new();
    public SymbolSearchResult SymbolResult { get; set; } = new();
    public ActivitySearchResult ActivityResult { get; set; } = new();
    public WorkflowSearchResult WorkflowResult { get; set; } = new();
    public Exception? ToThrow { get; set; }
    public string? LastProjectPath { get; private set; }
    public string? LastQuery { get; private set; }
    public string? LastKind { get; private set; }

    public Task<TextSearchResult> SearchTextAsync(string projectPath, string query, CancellationToken cancellationToken = default) {
        if (ToThrow is not null) throw ToThrow;
        LastProjectPath = projectPath; LastQuery = query;
        return Task.FromResult(TextResult);
    }

    public Task<SymbolSearchResult> SearchSymbolsAsync(string projectPath, string query, string? kind = null, CancellationToken cancellationToken = default) {
        if (ToThrow is not null) throw ToThrow;
        LastProjectPath = projectPath; LastQuery = query; LastKind = kind;
        return Task.FromResult(SymbolResult);
    }

    public Task<ActivitySearchResult> SearchActivitiesAsync(string projectPath, string query, CancellationToken cancellationToken = default) {
        if (ToThrow is not null) throw ToThrow;
        LastProjectPath = projectPath; LastQuery = query;
        return Task.FromResult(ActivityResult);
    }

    public Task<WorkflowSearchResult> SearchWorkflowsAsync(string projectPath, string query, CancellationToken cancellationToken = default) {
        if (ToThrow is not null) throw ToThrow;
        LastProjectPath = projectPath; LastQuery = query;
        return Task.FromResult(WorkflowResult);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/UiPath.Engineering.Mcp.Tools.Tests --filter "FullyQualifiedName~SearchCodebaseToolTests"`
Expected: FAIL — type `SearchCodebaseTool` does not exist (compile error).

- [ ] **Step 3: Add the error code and implement the tool**

In `src/UiPath.Engineering.Mcp.Core/ToolErrorCodes.cs`, add after `MutatingCommandDisabled`:

```csharp
public const string InvalidArgument = "INVALID_ARGUMENT";
```

Create `src/UiPath.Engineering.Mcp.Tools/SearchCodebaseTool.cs`:

```csharp
using System.ComponentModel;
using System.Diagnostics;
using ModelContextProtocol.Server;
using UiPath.Engineering.Mcp.Core;
using UiPath.Engineering.Mcp.Core.Abstractions;
using UiPath.Engineering.Mcp.Core.CodeSearch;
using UiPath.Engineering.Mcp.Core.Models;

namespace UiPath.Engineering.Mcp.Tools;

[McpServerToolType]
public sealed class SearchCodebaseTool {
    private readonly IFilesystemProvider _filesystem;
    private readonly ICodebaseSearchService _search;

    public SearchCodebaseTool(IFilesystemProvider filesystem, ICodebaseSearchService search) {
        _filesystem = filesystem;
        _search = search;
    }

    [McpServerTool, Description("Searches a UiPath project's .xaml and .cs files by case-insensitive substring. Modes: 'text' (matching lines), 'symbol' (C# symbols via Roslyn, optional kind filter), 'activity' (XAML activities by display name or type), 'workflow' (workflows by file name or description). For exact-name C# lookup prefer find_code_symbol / find_code_references.")]
    public async Task<ToolResult> SearchCodebase(
        [Description("Absolute path to the UiPath project directory.")] string projectPath,
        [Description("Case-insensitive substring to search for, e.g. 'queue'.")] string query,
        [Description("Search mode: text, symbol, activity, or workflow.")] string mode,
        [Description("Optional kind filter for symbol mode: method, property, field, class, interface.")] string? kind = null,
        CancellationToken cancellationToken = default) {
        var sw = Stopwatch.StartNew();

        if (ToolResults.GuardProject(_filesystem, projectPath, sw) is { } guardFailure) {
            return guardFailure;
        }
        if (string.IsNullOrWhiteSpace(query)) {
            return ToolResults.Failure("Query must not be empty.",
                [new ToolError(ToolErrorCodes.InvalidArgument,
                    "The 'query' parameter must not be empty.",
                    "Provide a non-empty search substring.")],
                sw);
        }

        try {
            switch (mode?.ToLowerInvariant()) {
                case "text": {
                    var result = await _search.SearchTextAsync(projectPath, query, cancellationToken);
                    var summary = $"Found {result.Matches.Count} text match(es) for '{query}' across {result.FilesSearched} file(s).";
                    return ToolResults.Ok(summary, result, sw, result.Warnings);
                }
                case "symbol": {
                    var result = await _search.SearchSymbolsAsync(projectPath, query, kind, cancellationToken);
                    var summary = $"Found {result.Matches.Count} symbol(s) matching '{query}'.";
                    return ToolResults.Ok(summary, result, sw, result.Warnings);
                }
                case "activity": {
                    var result = await _search.SearchActivitiesAsync(projectPath, query, cancellationToken);
                    var summary = $"Found {result.Matches.Count} activity match(es) for '{query}' across {result.WorkflowsSearched} workflow(s).";
                    return ToolResults.Ok(summary, result, sw, result.Warnings);
                }
                case "workflow": {
                    var result = await _search.SearchWorkflowsAsync(projectPath, query, cancellationToken);
                    var summary = $"Found {result.Matches.Count} workflow(s) matching '{query}'.";
                    return ToolResults.Ok(summary, result, sw, result.Warnings);
                }
                default:
                    return ToolResults.Failure($"Unknown search mode '{mode}'.",
                        [new ToolError(ToolErrorCodes.InvalidArgument,
                            $"Unknown search mode '{mode}'. Valid modes: text, symbol, activity, workflow.",
                            "Re-run with mode set to one of: text, symbol, activity, workflow.")],
                        sw);
            }
        } catch (Exception ex) {
            return ToolResults.FromException(ex, "Codebase search failed.", sw);
        }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/UiPath.Engineering.Mcp.Tools.Tests --filter "FullyQualifiedName~SearchCodebaseToolTests"`
Expected: PASS (10 tests).

- [ ] **Step 5: Commit**

```bash
git add src/UiPath.Engineering.Mcp.Core/ToolErrorCodes.cs src/UiPath.Engineering.Mcp.Tools/SearchCodebaseTool.cs tests/UiPath.Engineering.Mcp.Tools.Tests/Fakes.cs tests/UiPath.Engineering.Mcp.Tools.Tests/SearchCodebaseToolTests.cs
git commit -m "feat: add search_codebase MCP tool with text/symbol/activity/workflow modes"
```

---

### Task 5: DI registration + README + full suite

**Files:**
- Modify: `src/UiPath.Engineering.Mcp.Server/Program.cs` (one using + one registration line)
- Modify: `README.md` (tool table row, tool list, test-count text)

**Interfaces:**
- Consumes: `ICodebaseSearchService` / `CodebaseSearchService` (Tasks 1-3), whose ctor dependencies (`ICSharpContextBuilder`, `IProjectModelBuilder`, `IFilesystemProvider`) are all already registered as singletons in `Program.cs`.
- Produces: `search_codebase` discoverable via `WithToolsFromAssembly(typeof(AnalyzeProjectTool).Assembly)` (tool classes are auto-discovered; no per-tool registration exists).

- [ ] **Step 1: Register the service in DI**

In `src/UiPath.Engineering.Mcp.Server/Program.cs`:

Add to the using block (after `using UiPath.Engineering.Mcp.Core.CodeAnalysis;`):

```csharp
using UiPath.Engineering.Mcp.Core.CodeSearch;
```

Add after the `builder.Services.AddSingleton<ICSharpAnalysisService, CSharpAnalysisService>();` line:

```csharp
builder.Services.AddSingleton<ICodebaseSearchService, CodebaseSearchService>();
```

- [ ] **Step 2: Verify the server builds and the full suite passes**

Run: `dotnet build src/UiPath.Engineering.Mcp.Server`
Expected: build succeeds, 0 warnings/errors introduced.

Run: `dotnet test`
Expected: PASS — 479 tests total: 450 pre-existing + 29 new (6 SearchTextTests + 6 SearchSymbolsTests + 7 SearchActivitiesWorkflowsTests + 10 SearchCodebaseToolTests cases, where the blank-query theory contributes 2). Zero failures is the gate.

- [ ] **Step 3: Commit the DI change**

```bash
git add src/UiPath.Engineering.Mcp.Server/Program.cs
git commit -m "feat: register CodebaseSearchService in DI"
```

- [ ] **Step 4: Update the README**

In `README.md`, add this row to the tools table immediately after the `compile_project` row:

```markdown
| `search_codebase` | Substring search across a project's `.xaml` and `.cs` files in four modes: `text` (matching lines), `symbol` (C# symbols via Roslyn, optional `kind` filter), `activity` (XAML activities by display name/type), `workflow` (workflows by file name/description). Exact-case matches order first; capped at 200 matches with a `truncated` flag. |
```

In the tool list (the line ending with `` `compile_project` `` under the features/tools summary), change the trailing segment:

from: `` `get_compile_errors`, `compile_project` ``
to: `` `get_compile_errors`, `compile_project`, `search_codebase` ``

In the `UiPath.Engineering.Mcp.Tools.Tests` row of the test-projects table, change `All thirty tools:` to `All thirty-one tools:` and change the trailing segment:

from: `C# analysis tools (`find_code_symbol`, `find_code_references`, `get_code_context`, `get_compile_errors`, `compile_project`) over a fake analysis service, and structured error propagation (no raw exceptions).`
to: `C# analysis tools (`find_code_symbol`, `find_code_references`, `get_code_context`, `get_compile_errors`, `compile_project`) over a fake analysis service, `search_codebase` (guard failures, blank-query and unknown-mode `INVALID_ARGUMENT`, per-mode dispatch, structured error propagation), and structured error propagation (no raw exceptions).`

- [ ] **Step 5: Commit the README change**

```bash
git add README.md
git commit -m "docs: document search_codebase in README (31 tools)"
```

---

## Definition of Done Checklist

- [ ] All 5 tasks committed on the SP2 branch; no files outside each task's list staged.
- [ ] `dotnet test` green on the merged tree (450 pre-existing + new tests, zero failures).
- [ ] `search_codebase` answers "where is X generated / used" across `.xaml` and `.cs` via its four modes without reading raw files.
- [ ] README tool table, tool list, and test-count text updated.
