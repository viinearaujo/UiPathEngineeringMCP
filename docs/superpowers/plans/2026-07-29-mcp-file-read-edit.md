# MCP File Read & Targeted Edit Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add `read_workflow_file` (any text file, paginated, secret-redacted) and `edit_workflow_file` (exact string replacement for `.cs`/`.xaml`) tools so MCP clients like Microsoft 365 Copilot can inspect and modify project files without the user pasting content into chat.

**Architecture:** Two new `[McpServerToolType]` classes in `UiPath.Engineering.Mcp.Tools`, following the existing tool pattern (constructor-injected `IFilesystemProvider`, `ToolResults` guards, `Stopwatch`-timed results). A `SecretRedactor` static helper in `UiPath.Engineering.Mcp.Core` masks credential values before content is returned. Auto-discovered via the existing `WithToolsFromAssembly` scan — no `Program.cs` change.

**Tech Stack:** .NET 8, C#, ModelContextProtocol.Server, xUnit.

**Spec:** `docs/superpowers/specs/2026-07-29-mcp-file-read-edit-design.md`

## Global Constraints

- C# only, .NET 8; tools return structured JSON via `ToolResult`, never raw exceptions.
- No arbitrary shell execution; every path goes through `ToolResults.GuardProject` + `ToolResults.TryResolveWithinProject`.
- Secrets must never be returned in tool responses — redaction happens inside the read tool before building the result.
- `IFilesystemProvider.ReadAllText` already exists; do NOT change the provider interface or `Program.cs`.
- Test conventions: xUnit `[Fact]`, `FakeFilesystemProvider` in `tests/UiPath.Engineering.Mcp.Tools.Tests/Fakes.cs`, project path constant `"/projects/testProcess"`, data assertions via `JsonSerializer.SerializeToElement(result.Data)`.
- Do not modify `write_workflow_file` behavior.

---

### Task 1: SecretRedactor in Core

**Files:**
- Create: `src/UiPath.Engineering.Mcp.Core/SecretRedactor.cs`
- Test: `tests/UiPath.Engineering.Mcp.Core.Tests/SecretRedactorTests.cs`

**Interfaces:**
- Consumes: nothing (pure static helper).
- Produces: `UiPath.Engineering.Mcp.Core.SecretRedactor.Redact(string content) -> (string Text, int RedactedCount)` — used by Task 2's read tool.

- [ ] **Step 1: Write the failing tests**

```csharp
using UiPath.Engineering.Mcp.Core;

namespace UiPath.Engineering.Mcp.Core.Tests;

public class SecretRedactorTests {
    [Fact]
    public void Redact_JsonSecretValue_IsMasked() {
        var input = "{\n  \"LATAM_Password\": \"abc123\",\n  \"Proxy_Host\": \"mon-prod:9080\"\n}";

        var (text, count) = SecretRedactor.Redact(input);

        Assert.Equal(1, count);
        Assert.Contains("\"LATAM_Password\": \"***REDACTED***\"", text);
        Assert.Contains("\"Proxy_Host\": \"mon-prod:9080\"", text);
    }

    [Fact]
    public void Redact_KeyEqualsValue_IsMasked() {
        var input = "DbPassword=secret1\nRegion=us-east-1";

        var (text, count) = SecretRedactor.Redact(input);

        Assert.Equal(1, count);
        Assert.Contains("DbPassword=***REDACTED***", text);
        Assert.Contains("Region=us-east-1", text);
    }

    [Fact]
    public void Redact_NoSecrets_ReturnsInputUnchanged() {
        var input = "region=us-east-1\nhost=mon-prod-sqdrpavip-01";

        var (text, count) = SecretRedactor.Redact(input);

        Assert.Equal(0, count);
        Assert.Equal(input, text);
    }

    [Fact]
    public void Redact_MultipleSecrets_CountsEach() {
        var input = "ApiKey=aaa\nClientSecret=bbb";

        var (_, count) = SecretRedactor.Redact(input);

        Assert.Equal(2, count);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/UiPath.Engineering.Mcp.Core.Tests --filter SecretRedactorTests`
Expected: build failure — `SecretRedactor` does not exist.

- [ ] **Step 3: Implement SecretRedactor**

```csharp
using System.Text.RegularExpressions;

namespace UiPath.Engineering.Mcp.Core;

// Masks values of keys that look like credentials before file content is
// returned to an MCP client. Keys stay visible so structure remains useful.
public static class SecretRedactor {
    private const string KeyPattern =
        @"password|passwd|secret|token|apikey|api_key|accesskey|access_key|connectionstring|connection_string|clientsecret|client_secret|privatekey|private_key";

    // JSON-style: "somePasswordKey": "value"
    private static readonly Regex JsonPattern = new(
        $@"(""[^""]*(?:{KeyPattern})[^""]*""\s*:\s*"")[^""]*("")",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // key=value / key: value on a single line (unquoted key, value not already redacted)
    private static readonly Regex KeyValuePattern = new(
        $@"(?m)^\s*([^\s""'=:]+(?:{KeyPattern})[^\s""'=:]*\s*[=:]\s*)(?!\*)\S.*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static (string Text, int RedactedCount) Redact(string content) {
        var count = 0;
        var text = JsonPattern.Replace(content, m => {
            count++;
            return m.Groups[1].Value + "***REDACTED***" + m.Groups[2].Value;
        });
        text = KeyValuePattern.Replace(text, m => {
            count++;
            return m.Groups[1].Value + "***REDACTED***";
        });
        return (text, count);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/UiPath.Engineering.Mcp.Core.Tests --filter SecretRedactorTests`
Expected: 4 passed.

- [ ] **Step 5: Commit**

```bash
git add src/UiPath.Engineering.Mcp.Core/SecretRedactor.cs tests/UiPath.Engineering.Mcp.Core.Tests/SecretRedactorTests.cs
git commit -m "feat: add SecretRedactor for masking credential values in tool responses"
```

---

### Task 2: ReadWorkflowFileTool

**Files:**
- Create: `src/UiPath.Engineering.Mcp.Tools/ReadWorkflowFileTool.cs`
- Test: `tests/UiPath.Engineering.Mcp.Tools.Tests/ReadWorkflowFileToolTests.cs`

**Interfaces:**
- Consumes: `SecretRedactor.Redact` (Task 1), `IFilesystemProvider` (`IsPathAllowed`, `FindProjectJson`, `FileExists`, `ReadAllText`), `ToolResults.GuardProject` / `TryResolveWithinProject` / `Ok` / `Failure`.
- Produces: MCP tool method `ReadWorkflowFile(string projectPath, string relativePath, int? startLine = null, int? lineCount = null) -> ToolResult`. Success `Data` shape: `{ filePath, content, totalLines, returnedLines, truncated, redactedCount }` where `content` is line-numbered as `<line>\t<text>\n`.

- [ ] **Step 1: Write the failing tests**

```csharp
using System.Text.Json;

namespace UiPath.Engineering.Mcp.Tools.Tests;

public class ReadWorkflowFileToolTests {
    private const string ProjectPath = "/projects/testProcess";

    private static string Target(string relative) =>
        Path.Combine(Path.GetFullPath(ProjectPath), relative.Replace('/', Path.DirectorySeparatorChar));

    private static (FakeFilesystemProvider Fs, ReadWorkflowFileTool Tool) Create() {
        var fs = new FakeFilesystemProvider();
        return (fs, new ReadWorkflowFileTool(fs));
    }

    [Fact]
    public void ReadWorkflowFile_ReturnsLineNumberedContent() {
        var (fs, tool) = Create();
        fs.FileContents[Target("Main.cs")] = "line one\nline two";

        var result = tool.ReadWorkflowFile(ProjectPath, "Main.cs");
        var data = JsonSerializer.SerializeToElement(result.Data);

        Assert.Equal("success", result.Status);
        Assert.Equal("1\tline one\n2\tline two\n", data.GetProperty("content").GetString());
        Assert.Equal(2, data.GetProperty("totalLines").GetInt32());
        Assert.False(data.GetProperty("truncated").GetBoolean());
        Assert.Equal(0, data.GetProperty("redactedCount").GetInt32());
    }

    [Fact]
    public void ReadWorkflowFile_PaginatesWithStartLineAndLineCount() {
        var (fs, tool) = Create();
        fs.FileContents[Target("big.cs")] = string.Join("\n", Enumerable.Range(1, 10).Select(i => $"l{i}"));

        var result = tool.ReadWorkflowFile(ProjectPath, "big.cs", startLine: 4, lineCount: 2);
        var data = JsonSerializer.SerializeToElement(result.Data);

        Assert.Equal("success", result.Status);
        Assert.Equal("4\tl4\n5\tl5\n", data.GetProperty("content").GetString());
        Assert.True(data.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public void ReadWorkflowFile_RedactsSecrets() {
        var (fs, tool) = Create();
        fs.FileContents[Target("Data/Config.json")] =
            "{\n  \"LATAM_Password\": \"abc123\",\n  \"Proxy_Host\": \"mon-prod:9080\"\n}";

        var result = tool.ReadWorkflowFile(ProjectPath, "Data/Config.json");
        var data = JsonSerializer.SerializeToElement(result.Data);

        Assert.Equal("success", result.Status);
        Assert.Equal(1, data.GetProperty("redactedCount").GetInt32());
        Assert.DoesNotContain("abc123", data.GetProperty("content").GetString());
        Assert.Contains("mon-prod:9080", data.GetProperty("content").GetString());
    }

    [Fact]
    public void ReadWorkflowFile_RejectsEnvFile() {
        var (fs, tool) = Create();
        fs.FileContents[Target(".env")] = "X=1";

        var result = tool.ReadWorkflowFile(ProjectPath, ".env");

        Assert.Equal("error", result.Status);
    }

    [Fact]
    public void ReadWorkflowFile_RejectsBinaryContent() {
        var (fs, tool) = Create();
        fs.FileContents[Target("logo.png")] = "PNG\0binary";

        var result = tool.ReadWorkflowFile(ProjectPath, "logo.png");

        Assert.Equal("error", result.Status);
    }

    [Fact]
    public void ReadWorkflowFile_RejectsPathOutsideProject() {
        var (_, tool) = Create();

        var result = tool.ReadWorkflowFile(ProjectPath, "../../evil.cs");

        Assert.Equal("error", result.Status);
    }

    [Fact]
    public void ReadWorkflowFile_MissingFile_ReturnsError() {
        var (_, tool) = Create();

        var result = tool.ReadWorkflowFile(ProjectPath, "Nope.cs");

        Assert.Equal("error", result.Status);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/UiPath.Engineering.Mcp.Tools.Tests --filter ReadWorkflowFileToolTests`
Expected: build failure — `ReadWorkflowFileTool` does not exist.

- [ ] **Step 3: Implement ReadWorkflowFileTool**

```csharp
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using ModelContextProtocol.Server;
using UiPath.Engineering.Mcp.Core;
using UiPath.Engineering.Mcp.Core.Abstractions;
using UiPath.Engineering.Mcp.Core.Models;

namespace UiPath.Engineering.Mcp.Tools;

[McpServerToolType]
public sealed class ReadWorkflowFileTool {
    private const int DefaultMaxLines = 1000;
    private static readonly string[] BlockedExtensions = [".pem", ".key"];

    private readonly IFilesystemProvider _filesystem;

    public ReadWorkflowFileTool(IFilesystemProvider filesystem) {
        _filesystem = filesystem;
    }

    [McpServerTool, Description("Reads the text content of any file inside an existing UiPath project, with line numbers and pagination. Obvious secret values are redacted. Use startLine/lineCount to page through large files.")]
    public ToolResult ReadWorkflowFile(
        [Description("Absolute path to the UiPath project directory (must contain project.json).")] string projectPath,
        [Description("Path of the file relative to the project root, e.g. 'Main.cs' or 'Data/Config.json'.")] string relativePath,
        [Description("1-based first line to return; omit to start at line 1.")] int? startLine = null,
        [Description("Maximum number of lines to return (default 1000).")] int? lineCount = null) {

        var sw = Stopwatch.StartNew();

        if (ToolResults.GuardProject(_filesystem, projectPath, sw) is { } guardFailure) {
            return guardFailure;
        }

        if (string.IsNullOrWhiteSpace(relativePath)) {
            return ToolResults.Failure("relativePath is required.", sw);
        }

        var fileName = Path.GetFileName(relativePath);
        var extension = Path.GetExtension(relativePath);
        if (fileName.StartsWith(".env", StringComparison.OrdinalIgnoreCase)
            || fileName.Contains("credentials", StringComparison.OrdinalIgnoreCase)
            || BlockedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase)) {
            return ToolResults.Failure($"'{relativePath}' looks like a secret or key file and cannot be read.", sw);
        }

        if (!ToolResults.TryResolveWithinProject(projectPath, relativePath, out var targetPath)) {
            return ToolResults.Failure("relativePath must resolve to a location inside the project directory.", sw);
        }

        if (!_filesystem.FileExists(targetPath)) {
            return ToolResults.Failure($"File '{relativePath}' does not exist in the project.", sw);
        }

        string raw;
        try {
            raw = _filesystem.ReadAllText(targetPath);
        } catch (Exception) {
            return ToolResults.Failure($"'{relativePath}' could not be read as text (it may be binary).", sw);
        }

        if (raw.Contains('\0')) {
            return ToolResults.Failure($"'{relativePath}' appears to be a binary file; only text files can be read.", sw);
        }

        var (redacted, redactedCount) = SecretRedactor.Redact(raw);
        var lines = redacted.Replace("\r\n", "\n").Split('\n');

        var start = Math.Max(1, startLine ?? 1);
        if (start > lines.Length) {
            return ToolResults.Failure($"startLine {start} is past the end of the file ({lines.Length} lines).", sw);
        }

        var count = Math.Min(Math.Max(1, lineCount ?? DefaultMaxLines), lines.Length - start + 1);

        var sb = new StringBuilder();
        for (var i = 0; i < count; i++) {
            sb.Append(start + i).Append('\t').Append(lines[start - 1 + i]).Append('\n');
        }

        var truncated = start - 1 + count < lines.Length;

        return ToolResults.Ok(
            truncated
                ? $"Read lines {start}-{start + count - 1} of {lines.Length} from '{relativePath}' (truncated)."
                : $"Read {count} line(s) from '{relativePath}'.",
            new {
                filePath = targetPath,
                content = sb.ToString(),
                totalLines = lines.Length,
                returnedLines = count,
                truncated,
                redactedCount
            }, sw);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/UiPath.Engineering.Mcp.Tools.Tests --filter ReadWorkflowFileToolTests`
Expected: 7 passed.

- [ ] **Step 5: Commit**

```bash
git add src/UiPath.Engineering.Mcp.Tools/ReadWorkflowFileTool.cs tests/UiPath.Engineering.Mcp.Tools.Tests/ReadWorkflowFileToolTests.cs
git commit -m "feat: add read_workflow_file tool with pagination and secret redaction"
```

---

### Task 3: EditWorkflowFileTool

**Files:**
- Create: `src/UiPath.Engineering.Mcp.Tools/EditWorkflowFileTool.cs`
- Test: `tests/UiPath.Engineering.Mcp.Tools.Tests/EditWorkflowFileToolTests.cs`

**Interfaces:**
- Consumes: `IFilesystemProvider` (`FileExists`, `ReadAllText`, `WriteAllText`), `ToolResults` guards — same as Task 2.
- Produces: MCP tool method `EditWorkflowFile(string projectPath, string relativePath, string oldString, string newString, bool replaceAll = false) -> ToolResult`. Success `Data` shape: `{ filePath, replacements, bytesWritten }`.

- [ ] **Step 1: Write the failing tests**

```csharp
using System.Text.Json;

namespace UiPath.Engineering.Mcp.Tools.Tests;

public class EditWorkflowFileToolTests {
    private const string ProjectPath = "/projects/testProcess";

    private static string Target(string relative) =>
        Path.Combine(Path.GetFullPath(ProjectPath), relative.Replace('/', Path.DirectorySeparatorChar));

    [Fact]
    public void EditWorkflowFile_SingleMatch_Replaces() {
        var fs = new FakeFilesystemProvider();
        fs.FileContents[Target("Main.cs")] = "var a = 1;\nvar b = 2;";
        var tool = new EditWorkflowFileTool(fs);

        var result = tool.EditWorkflowFile(ProjectPath, "Main.cs", "var b = 2;", "var b = 3;");
        var data = JsonSerializer.SerializeToElement(result.Data);

        Assert.Equal("success", result.Status);
        Assert.Equal(1, data.GetProperty("replacements").GetInt32());
        Assert.Equal("var a = 1;\nvar b = 3;", fs.Writes[Target("Main.cs")]);
    }

    [Fact]
    public void EditWorkflowFile_ZeroMatches_ReturnsError() {
        var fs = new FakeFilesystemProvider();
        fs.FileContents[Target("Main.cs")] = "var a = 1;";
        var tool = new EditWorkflowFileTool(fs);

        var result = tool.EditWorkflowFile(ProjectPath, "Main.cs", "missing", "x");

        Assert.Equal("error", result.Status);
        Assert.Empty(fs.Writes);
    }

    [Fact]
    public void EditWorkflowFile_MultipleMatches_RequiresReplaceAll() {
        var fs = new FakeFilesystemProvider();
        fs.FileContents[Target("Main.cs")] = "foo\nfoo";
        var tool = new EditWorkflowFileTool(fs);

        var result = tool.EditWorkflowFile(ProjectPath, "Main.cs", "foo", "bar");

        Assert.Equal("error", result.Status);
        Assert.Contains("replaceAll", result.Errors[0]);
        Assert.Empty(fs.Writes);
    }

    [Fact]
    public void EditWorkflowFile_ReplaceAll_ReplacesEveryMatch() {
        var fs = new FakeFilesystemProvider();
        fs.FileContents[Target("Main.cs")] = "foo\nfoo\nfoo";
        var tool = new EditWorkflowFileTool(fs);

        var result = tool.EditWorkflowFile(ProjectPath, "Main.cs", "foo", "bar", replaceAll: true);
        var data = JsonSerializer.SerializeToElement(result.Data);

        Assert.Equal("success", result.Status);
        Assert.Equal(3, data.GetProperty("replacements").GetInt32());
        Assert.Equal("bar\nbar\nbar", fs.Writes[Target("Main.cs")]);
    }

    [Fact]
    public void EditWorkflowFile_RejectsDisallowedExtension() {
        var fs = new FakeFilesystemProvider();
        fs.FileContents[Target("Data/Config.json")] = "{}";
        var tool = new EditWorkflowFileTool(fs);

        var result = tool.EditWorkflowFile(ProjectPath, "Data/Config.json", "{}", "{ }");

        Assert.Equal("error", result.Status);
        Assert.Empty(fs.Writes);
    }

    [Fact]
    public void EditWorkflowFile_MissingFile_ReturnsError() {
        var fs = new FakeFilesystemProvider();
        var tool = new EditWorkflowFileTool(fs);

        var result = tool.EditWorkflowFile(ProjectPath, "Nope.cs", "a", "b");

        Assert.Equal("error", result.Status);
    }

    [Fact]
    public void EditWorkflowFile_RejectsPathOutsideProject() {
        var fs = new FakeFilesystemProvider();
        var tool = new EditWorkflowFileTool(fs);

        var result = tool.EditWorkflowFile(ProjectPath, "../../evil.cs", "a", "b");

        Assert.Equal("error", result.Status);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/UiPath.Engineering.Mcp.Tools.Tests --filter EditWorkflowFileToolTests`
Expected: build failure — `EditWorkflowFileTool` does not exist.

- [ ] **Step 3: Implement EditWorkflowFileTool**

```csharp
using System.ComponentModel;
using System.Diagnostics;
using ModelContextProtocol.Server;
using UiPath.Engineering.Mcp.Core.Abstractions;
using UiPath.Engineering.Mcp.Core.Models;

namespace UiPath.Engineering.Mcp.Tools;

[McpServerToolType]
public sealed class EditWorkflowFileTool {
    private static readonly string[] AllowedExtensions = [".xaml", ".cs"];

    private readonly IFilesystemProvider _filesystem;

    public EditWorkflowFileTool(IFilesystemProvider filesystem) {
        _filesystem = filesystem;
    }

    [McpServerTool, Description("Replaces an exact string in a .xaml or .cs workflow file inside an existing UiPath project. Fails when oldString is not found or matches multiple locations unless replaceAll is true. Prefer this over write_workflow_file for small changes.")]
    public ToolResult EditWorkflowFile(
        [Description("Absolute path to the UiPath project directory (must contain project.json).")] string projectPath,
        [Description("Path of the file relative to the project root, e.g. 'Main.xaml' or 'Workflows/SendEmail.cs'.")] string relativePath,
        [Description("Exact text to find; must match the file content byte-for-byte, including whitespace.")] string oldString,
        [Description("Replacement text.")] string newString,
        [Description("Replace every occurrence instead of requiring exactly one match.")] bool replaceAll = false) {

        var sw = Stopwatch.StartNew();

        if (ToolResults.GuardProject(_filesystem, projectPath, sw) is { } guardFailure) {
            return guardFailure;
        }

        if (string.IsNullOrWhiteSpace(relativePath)) {
            return ToolResults.Failure("relativePath is required.", sw);
        }

        var extension = Path.GetExtension(relativePath);
        if (!AllowedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase)) {
            return ToolResults.Failure($"Only {string.Join(" and ", AllowedExtensions)} files can be edited; got '{extension}'.", sw);
        }

        if (string.IsNullOrEmpty(oldString)) {
            return ToolResults.Failure("oldString is required.", sw);
        }

        if (!ToolResults.TryResolveWithinProject(projectPath, relativePath, out var targetPath)) {
            return ToolResults.Failure("relativePath must resolve to a location inside the project directory.", sw);
        }

        if (!_filesystem.FileExists(targetPath)) {
            return ToolResults.Failure($"File '{relativePath}' does not exist in the project.", sw);
        }

        var content = _filesystem.ReadAllText(targetPath);

        var matches = 0;
        var index = 0;
        while ((index = content.IndexOf(oldString, index, StringComparison.Ordinal)) >= 0) {
            matches++;
            index += oldString.Length;
        }

        if (matches == 0) {
            return ToolResults.Failure(
                "oldString was not found in the file. Read the file first to get its exact content, including whitespace.", sw);
        }

        if (matches > 1 && !replaceAll) {
            return ToolResults.Failure(
                $"oldString matches {matches} locations; make it more specific or pass replaceAll: true.", sw);
        }

        var updated = content.Replace(oldString, newString, StringComparison.Ordinal);
        _filesystem.WriteAllText(targetPath, updated);

        return ToolResults.Ok(
            $"Updated '{relativePath}' ({matches} replacement(s)).",
            new {
                filePath = targetPath,
                replacements = matches,
                bytesWritten = updated.Length
            }, sw);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/UiPath.Engineering.Mcp.Tools.Tests --filter EditWorkflowFileToolTests`
Expected: 7 passed.

- [ ] **Step 5: Commit**

```bash
git add src/UiPath.Engineering.Mcp.Tools/EditWorkflowFileTool.cs tests/UiPath.Engineering.Mcp.Tools.Tests/EditWorkflowFileToolTests.cs
git commit -m "feat: add edit_workflow_file tool for targeted string replacement"
```

---

### Task 4: README update and full verification

**Files:**
- Modify: `README.md` (tools table near line 9-29, tool count "Twenty tools" line 7, tools list line 153, test-project description line 235)

**Interfaces:**
- Consumes: tools from Tasks 2-3.
- Produces: documentation only.

- [ ] **Step 1: Update README**

In the tools table (after the `write_workflow_file` row), add:

```markdown
| `read_workflow_file` | Reads any text file inside a project with line numbers and pagination (`startLine`/`lineCount`, default 1000 lines); obvious secret values are redacted and `.env`/`*.pem`/`*.key` files are refused. |
| `edit_workflow_file` | Replaces an exact string in a `.xaml`/`.cs` file; fails on zero or ambiguous matches unless `replaceAll: true`. Preferred over `write_workflow_file` for small changes. |
```

Change "Twenty tools are implemented" to "Twenty-two tools are implemented", and append `read_workflow_file` and `edit_workflow_file` to the inline tool lists near line 153 and the test-project description near line 235.

- [ ] **Step 2: Full build and test run**

Run: `dotnet build UiPath.Engineering.Mcp.sln && dotnet test UiPath.Engineering.Mcp.sln`
Expected: build succeeds; all tests pass (14 new tests across Tasks 1-3 plus the existing suite).

- [ ] **Step 3: Commit**

```bash
git add README.md
git commit -m "docs: document read_workflow_file and edit_workflow_file tools"
```

---

## Self-Review Notes

- Spec coverage: read tool (§1) → Task 2; edit tool (§2) → Task 3; SecretRedactor (§3) → Task 1; no provider/Program.cs change (§4) respected; error handling via ToolResults (§5) in both tools; tests (§6) in Tasks 1-3; README (§7) → Task 4.
- Type consistency: `SecretRedactor.Redact(string) -> (string Text, int RedactedCount)` used identically in Task 2; tool method names match test calls; `Data` property names in tests match implementations.
- The `.env`-file test relies on `FakeFilesystemProvider.FileContents` populating `FileExists` — the read tool's name-based block runs before the existence check, so the test passes even without the fake containing the file.
