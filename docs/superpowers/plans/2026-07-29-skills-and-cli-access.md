# Skills Catalog + Allowlisted CLI Access Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give M365 Copilot on-demand access to the UiPath skills catalog (`list_skills` / `read_skill`) and a safely allowlisted `uip` CLI execution path (`run_uip_cli`).

**Architecture:** Follows the existing Core/Providers/Tools split. A new `SkillsProvider` (Providers/Skills) scans `<SkillsRoot>/*/SKILL.md` and serves the catalog; two thin tools expose it. A `CliCommandPolicy` (Providers/UiPathCli) classifies verb+subcommand as read-only/mutating/not-allowed from config; `run_uip_cli` delegates to the existing `IUiPathCliProvider.RunAsync`, which gains redacted, capped StdOut/StdErr capture.

**Tech Stack:** C# / .NET 8, ModelContextProtocol ASP.NET Core SDK, xUnit, Microsoft.Extensions.Options.

**Spec:** `docs/superpowers/specs/2026-07-29-skills-and-cli-access-design.md`

## Global Constraints

- C# only, .NET 8. No new NuGet dependencies (frontmatter is parsed by hand).
- Tools return the standard `ToolResult` envelope via `ToolResults.Ok` / `ToolResults.Failure` (`src/UiPath.Engineering.Mcp.Tools/ToolResults.cs`).
- Structured errors use `ToolError(ErrorCode, Message, FixHint, SuggestedTool?)` with codes declared in `src/UiPath.Engineering.Mcp.Core/ToolErrorCodes.cs`.
- All file content and process output returned to clients passes through `SecretRedactor.Redact(string)` → `(string Text, int RedactedCount)`.
- No arbitrary shell execution: only `uip` with allowlisted verbs, classification **fails closed** (unknown subcommand = mutating).
- Tests: xUnit (`[Fact]`), `Options.Create(...)` for options, fakes in `tests/UiPath.Engineering.Mcp.Tools.Tests/Fakes.cs`.
- Run tests with `dotnet test <test-project-path> --filter "<FullyQualifiedName~...>"`.
- Commit each task with `git add` of only that task's files. Ask the user before running any git commit.

---

### Task 1: SkillsOptions + SkillsProvider

**Files:**
- Create: `src/UiPath.Engineering.Mcp.Core/Configuration/SkillsOptions.cs`
- Create: `src/UiPath.Engineering.Mcp.Providers/Skills/SkillSummary.cs`
- Create: `src/UiPath.Engineering.Mcp.Providers/Skills/SkillReadResult.cs`
- Create: `src/UiPath.Engineering.Mcp.Providers/Skills/ISkillsProvider.cs`
- Create: `src/UiPath.Engineering.Mcp.Providers/Skills/SkillsProvider.cs`
- Test: `tests/UiPath.Engineering.Mcp.Providers.Tests/SkillsProviderTests.cs`

**Interfaces:**
- Consumes: nothing from other tasks.
- Produces (relied on by Task 2):
  - `record SkillSummary(string Name, string Description, string Directory)`
  - `class SkillReadResult { bool Success; string? ErrorCode; string? ErrorMessage; string SkillName; string File; string Content; bool Truncated; IReadOnlyList<string> AvailableSkills }`
  - `interface ISkillsProvider { Task<IReadOnlyList<SkillSummary>> ListAsync(CancellationToken = default); Task<SkillReadResult> ReadAsync(string name, string? file = null, CancellationToken = default); }`
  - Error codes used in `SkillReadResult.ErrorCode`: `SKILLS_ROOT_MISSING`, `SKILL_NOT_FOUND`, `SKILL_PATH_REJECTED`, `SKILL_FILE_NOT_FOUND`.

- [ ] **Step 1: Write the failing tests**

Create `tests/UiPath.Engineering.Mcp.Providers.Tests/SkillsProviderTests.cs`:

```csharp
using Microsoft.Extensions.Options;
using UiPath.Engineering.Mcp.Core.Configuration;
using UiPath.Engineering.Mcp.Providers.Skills;

namespace UiPath.Engineering.Mcp.Providers.Tests;

public class SkillsProviderTests : IDisposable {
    private readonly string _root;

    public SkillsProviderTests() {
        _root = Path.Combine(Path.GetTempPath(), "skills-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose() {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private SkillsProvider CreateSut(string? root = null, int maxBytes = 65536) =>
        new(Options.Create(new SkillsOptions { SkillsRoot = root ?? _root, MaxSkillFileBytes = maxBytes }));

    private string AddSkill(string dir, string frontmatterName, string description, string body = "# body") {
        var skillDir = Path.Combine(_root, dir);
        Directory.CreateDirectory(skillDir);
        File.WriteAllText(Path.Combine(skillDir, "SKILL.md"),
            $"---\nname: {frontmatterName}\ndescription: \"{description}\"\n---\n{body}\n");
        return skillDir;
    }

    [Fact]
    public async Task ListAsync_ParsesFrontmatterNameAndDescription() {
        AddSkill("uipath-rpa", "uipath-rpa", "UiPath RPA skill");

        var skills = await CreateSut().ListAsync();

        var skill = Assert.Single(skills);
        Assert.Equal("uipath-rpa", skill.Name);
        Assert.Equal("UiPath RPA skill", skill.Description);
        Assert.Equal("uipath-rpa", skill.Directory);
    }

    [Fact]
    public async Task ListAsync_MissingFrontmatter_FallsBackToDirectoryName() {
        var skillDir = Path.Combine(_root, "plain-skill");
        Directory.CreateDirectory(skillDir);
        File.WriteAllText(Path.Combine(skillDir, "SKILL.md"), "# no frontmatter\n");

        var skills = await CreateSut().ListAsync();

        var skill = Assert.Single(skills);
        Assert.Equal("plain-skill", skill.Name);
        Assert.Equal(string.Empty, skill.Description);
    }

    [Fact]
    public async Task ListAsync_MissingRoot_ThrowsDirectoryNotFound() {
        var sut = CreateSut(Path.Combine(_root, "does-not-exist"));

        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => sut.ListAsync());
    }

    [Fact]
    public async Task ReadAsync_ResolvesNameCaseInsensitively_AndDefaultsToSkillMd() {
        AddSkill("uipath-rpa", "uipath-rpa", "desc", body: "# playbook");

        var result = await CreateSut().ReadAsync("UIPATH-RPA");

        Assert.True(result.Success);
        Assert.Equal("uipath-rpa", result.SkillName);
        Assert.Equal("SKILL.md", result.File);
        Assert.Contains("# playbook", result.Content);
    }

    [Fact]
    public async Task ReadAsync_UnknownName_ReturnsNotFoundWithAvailableSkills() {
        AddSkill("uipath-rpa", "uipath-rpa", "desc");

        var result = await CreateSut().ReadAsync("nope");

        Assert.False(result.Success);
        Assert.Equal("SKILL_NOT_FOUND", result.ErrorCode);
        Assert.Contains("uipath-rpa", result.AvailableSkills);
    }

    [Fact]
    public async Task ReadAsync_AuxiliaryFileInsideSkillDir_IsRead() {
        var skillDir = AddSkill("uipath-platform", "uipath-platform", "desc");
        Directory.CreateDirectory(Path.Combine(skillDir, "references"));
        File.WriteAllText(Path.Combine(skillDir, "references", "auth.md"), "# auth details");

        var result = await CreateSut().ReadAsync("uipath-platform", "references/auth.md");

        Assert.True(result.Success);
        Assert.Contains("# auth details", result.Content);
    }

    [Fact]
    public async Task ReadAsync_PathEscapingSkillDir_IsRejected() {
        AddSkill("uipath-rpa", "uipath-rpa", "desc");

        var result = await CreateSut().ReadAsync("uipath-rpa", "../../secret.txt");

        Assert.False(result.Success);
        Assert.Equal("SKILL_PATH_REJECTED", result.ErrorCode);
    }

    [Fact]
    public async Task ReadAsync_OversizedFile_IsTruncatedWithMarker() {
        var skillDir = Path.Combine(_root, "big-skill");
        Directory.CreateDirectory(skillDir);
        File.WriteAllText(Path.Combine(skillDir, "SKILL.md"), new string('x', 500));

        var result = await CreateSut(maxBytes: 100).ReadAsync("big-skill");

        Assert.True(result.Success);
        Assert.True(result.Truncated);
        Assert.Contains("[truncated]", result.Content);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/UiPath.Engineering.Mcp.Providers.Tests --filter "FullyQualifiedName~SkillsProviderTests"`
Expected: build failure — `SkillsProvider`, `SkillsOptions`, `SkillSummary`, `SkillReadResult` do not exist.

- [ ] **Step 3: Write the implementation**

Create `src/UiPath.Engineering.Mcp.Core/Configuration/SkillsOptions.cs`:

```csharp
namespace UiPath.Engineering.Mcp.Core.Configuration;
public sealed class SkillsOptions {
    // Resolved against the server working directory when relative.
    public string SkillsRoot { get; init; } = ".agents/skills";
    // Character cap applied to any single skill file read.
    public int MaxSkillFileBytes { get; init; } = 65536;
}
```

Create `src/UiPath.Engineering.Mcp.Providers/Skills/SkillSummary.cs`:

```csharp
namespace UiPath.Engineering.Mcp.Providers.Skills;
public sealed record SkillSummary(string Name, string Description, string Directory);
```

Create `src/UiPath.Engineering.Mcp.Providers/Skills/SkillReadResult.cs`:

```csharp
namespace UiPath.Engineering.Mcp.Providers.Skills;
public sealed class SkillReadResult {
    public bool Success { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public string SkillName { get; init; } = string.Empty;
    public string File { get; init; } = string.Empty;
    public string Content { get; init; } = string.Empty;
    public bool Truncated { get; init; }
    public IReadOnlyList<string> AvailableSkills { get; init; } = [];
}
```

Create `src/UiPath.Engineering.Mcp.Providers/Skills/ISkillsProvider.cs`:

```csharp
namespace UiPath.Engineering.Mcp.Providers.Skills;
public interface ISkillsProvider {
    Task<IReadOnlyList<SkillSummary>> ListAsync(CancellationToken cancellationToken = default);
    Task<SkillReadResult> ReadAsync(string name, string? file = null, CancellationToken cancellationToken = default);
}
```

Create `src/UiPath.Engineering.Mcp.Providers/Skills/SkillsProvider.cs`:

```csharp
using Microsoft.Extensions.Options;
using UiPath.Engineering.Mcp.Core.Configuration;

namespace UiPath.Engineering.Mcp.Providers.Skills;

// Serves the uip skills catalog (<SkillsRoot>/*/SKILL.md) to MCP tools.
// Re-scans per call: a directory listing plus a frontmatter header parse is
// cheap and avoids cache invalidation complexity.
public sealed class SkillsProvider : ISkillsProvider {
    private readonly SkillsOptions _options;

    public SkillsProvider(IOptions<SkillsOptions> options) {
        _options = options.Value;
    }

    private string ResolvedRoot => Path.GetFullPath(_options.SkillsRoot);

    public Task<IReadOnlyList<SkillSummary>> ListAsync(CancellationToken cancellationToken = default) {
        var root = ResolvedRoot;
        if (!System.IO.Directory.Exists(root)) {
            throw new DirectoryNotFoundException($"Skills root '{root}' does not exist.");
        }

        var summaries = new List<SkillSummary>();
        foreach (var dir in System.IO.Directory.EnumerateDirectories(root)) {
            cancellationToken.ThrowIfCancellationRequested();
            var skillFile = Path.Combine(dir, "SKILL.md");
            if (!System.IO.File.Exists(skillFile)) {
                continue;
            }

            var (name, description) = ParseFrontmatter(skillFile);
            summaries.Add(new SkillSummary(
                name ?? Path.GetFileName(dir),
                description ?? string.Empty,
                Path.GetFileName(dir)));
        }

        IReadOnlyList<SkillSummary> result = summaries
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return Task.FromResult(result);
    }

    public async Task<SkillReadResult> ReadAsync(string name, string? file = null, CancellationToken cancellationToken = default) {
        var root = ResolvedRoot;
        if (!System.IO.Directory.Exists(root)) {
            return new SkillReadResult {
                ErrorCode = "SKILLS_ROOT_MISSING",
                ErrorMessage = $"Skills root '{root}' does not exist."
            };
        }

        var summaries = await ListAsync(cancellationToken);
        var match = summaries.FirstOrDefault(s =>
            s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
            || s.Directory.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (match is null) {
            return new SkillReadResult {
                ErrorCode = "SKILL_NOT_FOUND",
                ErrorMessage = $"Skill '{name}' was not found.",
                AvailableSkills = summaries.Select(s => s.Name).ToList()
            };
        }

        var relative = string.IsNullOrWhiteSpace(file) ? "SKILL.md" : file;
        var skillDir = Path.Combine(root, match.Directory);
        var target = Path.GetFullPath(Path.Combine(skillDir, relative.Replace('/', Path.DirectorySeparatorChar)));

        // Confinement: the resolved path must stay inside the skill directory.
        if (!target.StartsWith(skillDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) {
            return new SkillReadResult {
                ErrorCode = "SKILL_PATH_REJECTED",
                ErrorMessage = $"'{relative}' escapes the skill directory."
            };
        }

        if (!System.IO.File.Exists(target)) {
            return new SkillReadResult {
                ErrorCode = "SKILL_FILE_NOT_FOUND",
                ErrorMessage = $"'{relative}' does not exist in skill '{match.Name}'."
            };
        }

        var content = System.IO.File.ReadAllText(target);
        var truncated = content.Length > _options.MaxSkillFileBytes;
        if (truncated) {
            content = content[.._options.MaxSkillFileBytes] + "\n...[truncated]";
        }

        return new SkillReadResult {
            Success = true,
            SkillName = match.Name,
            File = relative,
            Content = content,
            Truncated = truncated
        };
    }

    // Minimal frontmatter reader: only the name/description keys between the
    // leading --- markers. No YAML dependency for two scalar keys.
    private static (string? Name, string? Description) ParseFrontmatter(string skillFile) {
        string? name = null, description = null;
        using var reader = new StreamReader(skillFile);
        if (reader.ReadLine()?.Trim() != "---") {
            return (null, null);
        }

        string? line;
        while ((line = reader.ReadLine()) is not null) {
            if (line.Trim() == "---") {
                break;
            }

            var colon = line.IndexOf(':');
            if (colon <= 0) {
                continue;
            }

            var key = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim().Trim('"');
            if (key.Equals("name", StringComparison.OrdinalIgnoreCase)) {
                name = value;
            } else if (key.Equals("description", StringComparison.OrdinalIgnoreCase)) {
                description = value;
            }
        }

        return (name, description);
    }
}
```

(`System.IO.` prefixes are used because the property `SkillReadResult.File`/local names would otherwise shadow nothing here, but `Directory` collides with the `SkillSummary.Directory` record property name in some contexts — the prefixes keep it unambiguous.)

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/UiPath.Engineering.Mcp.Providers.Tests --filter "FullyQualifiedName~SkillsProviderTests"`
Expected: 8 tests PASS.

- [ ] **Step 5: Commit**

```bash
git add src/UiPath.Engineering.Mcp.Core/Configuration/SkillsOptions.cs src/UiPath.Engineering.Mcp.Providers/Skills/ tests/UiPath.Engineering.Mcp.Providers.Tests/SkillsProviderTests.cs
git commit -m "feat: add SkillsProvider serving the uip skills catalog"
```

---

### Task 2: list_skills + read_skill tools

**Files:**
- Modify: `src/UiPath.Engineering.Mcp.Core/ToolErrorCodes.cs`
- Create: `src/UiPath.Engineering.Mcp.Tools/ListSkillsTool.cs`
- Create: `src/UiPath.Engineering.Mcp.Tools/ReadSkillTool.cs`
- Modify: `tests/UiPath.Engineering.Mcp.Tools.Tests/Fakes.cs`
- Test: `tests/UiPath.Engineering.Mcp.Tools.Tests/SkillsToolsTests.cs`

**Interfaces:**
- Consumes (from Task 1): `ISkillsProvider`, `SkillSummary`, `SkillReadResult` with error codes `SKILLS_ROOT_MISSING` / `SKILL_NOT_FOUND` / `SKILL_PATH_REJECTED` / `SKILL_FILE_NOT_FOUND`.
- Produces: `ToolErrorCodes.SkillsRootMissing`, `.SkillNotFound`, `.SkillPathRejected`, `.SkillFileNotFound` (constants, string values identical to the codes above).

- [ ] **Step 1: Add the error codes**

In `src/UiPath.Engineering.Mcp.Core/ToolErrorCodes.cs`, append inside the class:

```csharp
    public const string SkillsRootMissing = "SKILLS_ROOT_MISSING";
    public const string SkillNotFound = "SKILL_NOT_FOUND";
    public const string SkillPathRejected = "SKILL_PATH_REJECTED";
    public const string SkillFileNotFound = "SKILL_FILE_NOT_FOUND";
```

- [ ] **Step 2: Add FakeSkillsProvider to Fakes.cs**

Append to `tests/UiPath.Engineering.Mcp.Tools.Tests/Fakes.cs`:

```csharp
internal sealed class FakeSkillsProvider : ISkillsProvider {
    public IReadOnlyList<SkillSummary> Skills { get; set; } = [];
    public SkillReadResult ReadResult { get; set; } = new() {
        Success = true, SkillName = "uipath-rpa", File = "SKILL.md", Content = "# playbook"
    };
    public bool ThrowRootMissing { get; set; }
    public string? LastName { get; private set; }
    public string? LastFile { get; private set; }

    public Task<IReadOnlyList<SkillSummary>> ListAsync(CancellationToken cancellationToken = default) {
        if (ThrowRootMissing) {
            throw new DirectoryNotFoundException("Skills root '/missing' does not exist.");
        }
        return Task.FromResult(Skills);
    }

    public Task<SkillReadResult> ReadAsync(string name, string? file = null, CancellationToken cancellationToken = default) {
        LastName = name;
        LastFile = file;
        return Task.FromResult(ReadResult);
    }
}
```

Add `using UiPath.Engineering.Mcp.Providers.Skills;` to the usings at the top of `Fakes.cs`.

- [ ] **Step 3: Write the failing tests**

Create `tests/UiPath.Engineering.Mcp.Tools.Tests/SkillsToolsTests.cs`:

```csharp
using UiPath.Engineering.Mcp.Providers.Skills;
using UiPath.Engineering.Mcp.Tools;

namespace UiPath.Engineering.Mcp.Tools.Tests;

public class SkillsToolsTests {
    [Fact]
    public async Task ListSkills_ReturnsCatalog() {
        var skills = new FakeSkillsProvider {
            Skills = [new SkillSummary("uipath-rpa", "RPA skill", "uipath-rpa")]
        };
        var sut = new ListSkillsTool(skills);

        var result = await sut.ListSkills();

        Assert.Equal("success", result.Status);
        Assert.Contains("1 skill", result.Summary);
    }

    [Fact]
    public async Task ListSkills_MissingRoot_ReturnsStructuredError() {
        var skills = new FakeSkillsProvider { ThrowRootMissing = true };
        var sut = new ListSkillsTool(skills);

        var result = await sut.ListSkills();

        Assert.Equal("error", result.Status);
        Assert.Contains(result.ErrorDetails, e => e.ErrorCode == "SKILLS_ROOT_MISSING");
    }

    [Fact]
    public async Task ReadSkill_Success_ReturnsRedactedContent() {
        var skills = new FakeSkillsProvider {
            ReadResult = new SkillReadResult {
                Success = true, SkillName = "uipath-rpa", File = "SKILL.md",
                Content = "playbook with password=hunter2 inside"
            }
        };
        var sut = new ReadSkillTool(skills);

        var result = await sut.ReadSkill("uipath-rpa");

        Assert.Equal("success", result.Status);
        Assert.Equal("uipath-rpa", skills.LastName);
        var data = result.Data!.ToString()!;
        Assert.DoesNotContain("hunter2", data);
    }

    [Fact]
    public async Task ReadSkill_UnknownSkill_SuggestsListSkills() {
        var skills = new FakeSkillsProvider {
            ReadResult = new SkillReadResult {
                ErrorCode = "SKILL_NOT_FOUND", ErrorMessage = "Skill 'nope' was not found.",
                AvailableSkills = ["uipath-rpa"]
            }
        };
        var sut = new ReadSkillTool(skills);

        var result = await sut.ReadSkill("nope");

        Assert.Equal("error", result.Status);
        var error = Assert.Single(result.ErrorDetails);
        Assert.Equal("SKILL_NOT_FOUND", error.ErrorCode);
        Assert.Equal("list_skills", error.SuggestedTool);
    }

    [Fact]
    public async Task ReadSkill_PathRejected_ReturnsStructuredError() {
        var skills = new FakeSkillsProvider {
            ReadResult = new SkillReadResult {
                ErrorCode = "SKILL_PATH_REJECTED", ErrorMessage = "'../x' escapes the skill directory."
            }
        };
        var sut = new ReadSkillTool(skills);

        var result = await sut.ReadSkill("uipath-rpa", "../x");

        Assert.Equal("error", result.Status);
        Assert.Contains(result.ErrorDetails, e => e.ErrorCode == "SKILL_PATH_REJECTED");
    }
}
```

- [ ] **Step 4: Run tests to verify they fail**

Run: `dotnet test tests/UiPath.Engineering.Mcp.Tools.Tests --filter "FullyQualifiedName~SkillsToolsTests"`
Expected: build failure — `ListSkillsTool` and `ReadSkillTool` do not exist.

- [ ] **Step 5: Write the tools**

Create `src/UiPath.Engineering.Mcp.Tools/ListSkillsTool.cs`:

```csharp
using System.ComponentModel;
using System.Diagnostics;
using ModelContextProtocol.Server;
using UiPath.Engineering.Mcp.Core;
using UiPath.Engineering.Mcp.Core.Models;
using UiPath.Engineering.Mcp.Providers.Skills;

namespace UiPath.Engineering.Mcp.Tools;

[McpServerToolType]
public sealed class ListSkillsTool {
    private readonly ISkillsProvider _skills;

    public ListSkillsTool(ISkillsProvider skills) {
        _skills = skills;
    }

    [McpServerTool, Description("Lists the UiPath skills catalog (name + description) — the playbooks for UiPath tasks. Call read_skill with a name from this list to load the full instructions before doing UiPath work.")]
    public async Task<ToolResult> ListSkills(CancellationToken cancellationToken = default) {
        var sw = Stopwatch.StartNew();

        IReadOnlyList<SkillSummary> skills;
        try {
            skills = await _skills.ListAsync(cancellationToken);
        } catch (DirectoryNotFoundException ex) {
            return ToolResults.Failure("Skills root not found.",
                [new ToolError(ToolErrorCodes.SkillsRootMissing, ex.Message,
                    "Set Skills:SkillsRoot in appsettings.json to a directory containing */SKILL.md.")], sw);
        }

        return ToolResults.Ok($"Found {skills.Count} skill(s).",
            new { skills = skills.Select(s => new { s.Name, s.Description, s.Directory }).ToList() }, sw);
    }
}
```

Create `src/UiPath.Engineering.Mcp.Tools/ReadSkillTool.cs`:

```csharp
using System.ComponentModel;
using System.Diagnostics;
using ModelContextProtocol.Server;
using UiPath.Engineering.Mcp.Core;
using UiPath.Engineering.Mcp.Core.Models;
using UiPath.Engineering.Mcp.Providers.Skills;

namespace UiPath.Engineering.Mcp.Tools;

[McpServerToolType]
public sealed class ReadSkillTool {
    private readonly ISkillsProvider _skills;

    public ReadSkillTool(ISkillsProvider skills) {
        _skills = skills;
    }

    [McpServerTool, Description("Reads the full content of a UiPath skill (its SKILL.md playbook, or an auxiliary file inside the skill directory via the file parameter). Use list_skills first to discover names.")]
    public async Task<ToolResult> ReadSkill(
        [Description("Skill name or directory, e.g. 'uipath-rpa' (case-insensitive).")] string name,
        [Description("Optional file inside the skill directory, e.g. 'references/auth.md'. Defaults to SKILL.md.")] string? file = null,
        CancellationToken cancellationToken = default) {

        var sw = Stopwatch.StartNew();

        var result = await _skills.ReadAsync(name, file, cancellationToken);
        if (!result.Success) {
            return ToolResults.Failure(result.ErrorMessage ?? "Skill read failed.", [MapError(result)], sw);
        }

        var (redacted, redactedCount) = SecretRedactor.Redact(result.Content);
        return ToolResults.Ok($"Read '{result.File}' from skill '{result.SkillName}'.",
            new {
                name = result.SkillName,
                file = result.File,
                content = redacted,
                truncated = result.Truncated,
                redactedCount
            }, sw);
    }

    private static ToolError MapError(SkillReadResult result) => result.ErrorCode switch {
        "SKILL_NOT_FOUND" => new ToolError(ToolErrorCodes.SkillNotFound, result.ErrorMessage!,
            $"Pick one of the available skills: {string.Join(", ", result.AvailableSkills)}.", "list_skills"),
        "SKILL_PATH_REJECTED" => new ToolError(ToolErrorCodes.SkillPathRejected, result.ErrorMessage!,
            "Pass a file path inside the skill directory, without '..' or absolute paths."),
        "SKILL_FILE_NOT_FOUND" => new ToolError(ToolErrorCodes.SkillFileNotFound, result.ErrorMessage!,
            "Check the file name against the skill directory contents; default is SKILL.md."),
        _ => new ToolError(ToolErrorCodes.SkillsRootMissing, result.ErrorMessage!,
            "Set Skills:SkillsRoot in appsettings.json to a directory containing */SKILL.md.")
    };
}
```

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test tests/UiPath.Engineering.Mcp.Tools.Tests --filter "FullyQualifiedName~SkillsToolsTests"`
Expected: 5 tests PASS.

- [ ] **Step 7: Commit**

```bash
git add src/UiPath.Engineering.Mcp.Core/ToolErrorCodes.cs src/UiPath.Engineering.Mcp.Tools/ListSkillsTool.cs src/UiPath.Engineering.Mcp.Tools/ReadSkillTool.cs tests/UiPath.Engineering.Mcp.Tools.Tests/Fakes.cs tests/UiPath.Engineering.Mcp.Tools.Tests/SkillsToolsTests.cs
git commit -m "feat: add list_skills and read_skill tools for the UiPath skills catalog"
```

---

### Task 3: CliCommandPolicy + CLI options + redacted/capped stdout capture

**Files:**
- Modify: `src/UiPath.Engineering.Mcp.Core/Configuration/UiPathCliOptions.cs`
- Modify: `src/UiPath.Engineering.Mcp.Providers/UiPathCli/UiPathCliResult.cs`
- Modify: `src/UiPath.Engineering.Mcp.Providers/UiPathCli/UiPathCliProvider.cs`
- Create: `src/UiPath.Engineering.Mcp.Providers/UiPathCli/CliCommandPolicy.cs`
- Test: `tests/UiPath.Engineering.Mcp.Providers.Tests/CliCommandPolicyTests.cs`
- Test: `tests/UiPath.Engineering.Mcp.Providers.Tests/UiPathCliProviderTests.cs` (append)

**Interfaces:**
- Consumes: nothing from Tasks 1-2.
- Produces (relied on by Task 4):
  - `enum CliCommandClass { AllowedReadOnly, AllowedMutating, VerbNotAllowed }`
  - `class CliCommandPolicy { CliCommandPolicy(UiPathCliOptions options); CliCommandClass Classify(string verb, string arguments); }`
  - `UiPathCliResult.StdOut` / `.StdErr` (string, redacted, capped at `UiPathCliOptions.MaxOutputChars`)
  - `UiPathCliOptions.AllowedVerbs` (string[]), `.ReadOnlySubcommands` (Dictionary<string, string[]>), `.EnableMutatingCommands` (bool), `.MaxOutputChars` (int)

- [ ] **Step 1: Write the failing tests**

Create `tests/UiPath.Engineering.Mcp.Providers.Tests/CliCommandPolicyTests.cs`:

```csharp
using UiPath.Engineering.Mcp.Core.Configuration;
using UiPath.Engineering.Mcp.Providers.UiPathCli;

namespace UiPath.Engineering.Mcp.Providers.Tests;

public class CliCommandPolicyTests {
    private static CliCommandPolicy CreateSut(Action<UiPathCliOptions>? configure = null) {
        var options = new UiPathCliOptions();
        configure?.Invoke(options);
        return new CliCommandPolicy(options);
    }

    [Fact]
    public void Classify_ReadOnlySubcommand_IsAllowedReadOnly() {
        var sut = CreateSut();

        Assert.Equal(CliCommandClass.AllowedReadOnly,
            sut.Classify("rpa", "validate --project-dir \"C:/proj\" --output json"));
    }

    [Fact]
    public void Classify_VerbMatchingIsCaseInsensitive() {
        var sut = CreateSut();

        Assert.Equal(CliCommandClass.AllowedReadOnly, sut.Classify("RPA", "VALIDATE --project-dir x"));
    }

    [Fact]
    public void Classify_KnownMutatingSubcommand_IsAllowedMutating() {
        var sut = CreateSut();

        Assert.Equal(CliCommandClass.AllowedMutating, sut.Classify("solution", "publish --output json"));
    }

    [Fact]
    public void Classify_UnknownSubcommand_FailsClosedAsMutating() {
        var sut = CreateSut();

        Assert.Equal(CliCommandClass.AllowedMutating, sut.Classify("rpa", "some-brand-new-verb"));
    }

    [Fact]
    public void Classify_EmptyArguments_FailsClosedAsMutating() {
        var sut = CreateSut();

        Assert.Equal(CliCommandClass.AllowedMutating, sut.Classify("rpa", "   "));
    }

    [Fact]
    public void Classify_VerbOutsideAllowlist_IsVerbNotAllowed() {
        var sut = CreateSut();

        Assert.Equal(CliCommandClass.VerbNotAllowed, sut.Classify("orx", "assets list"));
    }
}
```

Append to `tests/UiPath.Engineering.Mcp.Providers.Tests/UiPathCliProviderTests.cs`:

```csharp
    [Fact]
    public void CaptureOutput_RedactsSecrets_AndCapsLength() {
        var (stdout, _) = UiPathCliProvider.CaptureOutput(
            "token=abc123secret", "", maxChars: 50);

        Assert.DoesNotContain("abc123secret", stdout);

        var (capped, _) = UiPathCliProvider.CaptureOutput(new string('y', 500), "", maxChars: 100);
        Assert.True(capped.Length < 500);
        Assert.Contains("[truncated]", capped);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/UiPath.Engineering.Mcp.Providers.Tests --filter "FullyQualifiedName~CliCommandPolicyTests|FullyQualifiedName~UiPathCliProviderTests"`
Expected: build failure — `CliCommandPolicy`, `CliCommandClass`, `UiPathCliProvider.CaptureOutput` do not exist.

- [ ] **Step 3: Write the implementation**

Replace `src/UiPath.Engineering.Mcp.Core/Configuration/UiPathCliOptions.cs` with:

```csharp
namespace UiPath.Engineering.Mcp.Core.Configuration;
public sealed class UiPathCliOptions {
    public string ExecutablePath { get; init; } = "uip";
    public int DefaultTimeoutSeconds { get; init; } = 300;
    public bool IncludeRawOutput { get; init; }

    // run_uip_cli allowlist. Only these top-level uip verbs may execute.
    public string[] AllowedVerbs { get; init; } = ["rpa", "solution"];

    // Subcommands of an allowed verb that run without EnableMutatingCommands.
    // Anything not listed here is classified as mutating (fail closed).
    public Dictionary<string, string[]> ReadOnlySubcommands { get; init; } = new(StringComparer.OrdinalIgnoreCase) {
        ["rpa"] = ["analyze", "validate", "build"],
        ["solution"] = ["list", "status"]
    };

    // Master switch for mutating subcommands (pack, publish, deploy, delete...).
    public bool EnableMutatingCommands { get; init; }

    // Character cap applied to each of stdout/stderr in run_uip_cli responses.
    public int MaxOutputChars { get; init; } = 32768;
}
```

Create `src/UiPath.Engineering.Mcp.Providers/UiPathCli/CliCommandPolicy.cs`:

```csharp
using UiPath.Engineering.Mcp.Core.Configuration;

namespace UiPath.Engineering.Mcp.Providers.UiPathCli;

public enum CliCommandClass { AllowedReadOnly, AllowedMutating, VerbNotAllowed }

// Decides whether a `uip <verb> <args>` invocation may run, based on
// UiPathCliOptions. Fails closed: unknown subcommands are treated as mutating.
public sealed class CliCommandPolicy {
    private readonly UiPathCliOptions _options;

    public CliCommandPolicy(UiPathCliOptions options) {
        _options = options;
    }

    public CliCommandClass Classify(string verb, string arguments) {
        if (!_options.AllowedVerbs.Contains(verb, StringComparer.OrdinalIgnoreCase)) {
            return CliCommandClass.VerbNotAllowed;
        }

        var subcommand = arguments.TrimStart()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault() ?? string.Empty;

        if (_options.ReadOnlySubcommands.TryGetValue(verb, out var readOnly)
            && readOnly.Contains(subcommand, StringComparer.OrdinalIgnoreCase)) {
            return CliCommandClass.AllowedReadOnly;
        }

        return CliCommandClass.AllowedMutating;
    }
}
```

Add to `src/UiPath.Engineering.Mcp.Providers/UiPathCli/UiPathCliResult.cs`:

```csharp
    public string StdOut { get; init; } = string.Empty;
    public string StdErr { get; init; } = string.Empty;
```

In `src/UiPath.Engineering.Mcp.Providers/UiPathCli/UiPathCliProvider.cs`:

1. Add the using for SecretRedactor at the top: `using UiPath.Engineering.Mcp.Core;`
2. Add the internal capture helper (used by RunAsync, directly unit-testable):

```csharp
    // Redacts secrets and caps each stream so tool responses stay bounded.
    internal static (string StdOut, string StdErr) CaptureOutput(string stdout, string stderr, int maxChars) {
        var (redactedOut, _) = SecretRedactor.Redact(stdout);
        var (redactedErr, _) = SecretRedactor.Redact(stderr);
        return (Cap(redactedOut, maxChars), Cap(redactedErr, maxChars));
    }

    private static string Cap(string s, int maxChars) =>
        s.Length <= maxChars ? s : s[..maxChars] + "\n...[truncated]";
```

3. In `RunAsync`, in the final `return new UiPathCliResult { ... }` block, populate the new fields. Insert before the return:

```csharp
        var (stdout, stderr) = CaptureOutput(run.StdOut, run.StdErr, _options.MaxOutputChars);
```

and add to the initializer:

```csharp
            StdOut = stdout,
            StdErr = stderr,
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/UiPath.Engineering.Mcp.Providers.Tests --filter "FullyQualifiedName~CliCommandPolicyTests|FullyQualifiedName~UiPathCliProviderTests"`
Expected: all PASS (6 policy tests + the new capture test + existing provider tests).

- [ ] **Step 5: Commit**

```bash
git add src/UiPath.Engineering.Mcp.Core/Configuration/UiPathCliOptions.cs src/UiPath.Engineering.Mcp.Providers/UiPathCli/ tests/UiPath.Engineering.Mcp.Providers.Tests/CliCommandPolicyTests.cs tests/UiPath.Engineering.Mcp.Providers.Tests/UiPathCliProviderTests.cs
git commit -m "feat: add CliCommandPolicy and redacted capped stdout capture for CLI runs"
```

---

### Task 4: run_uip_cli tool + DI registration + appsettings

**Files:**
- Modify: `src/UiPath.Engineering.Mcp.Core/ToolErrorCodes.cs`
- Create: `src/UiPath.Engineering.Mcp.Tools/RunUiPathCliTool.cs`
- Modify: `src/UiPath.Engineering.Mcp.Server/Program.cs`
- Modify: `src/UiPath.Engineering.Mcp.Server/appsettings.json`
- Test: `tests/UiPath.Engineering.Mcp.Tools.Tests/RunUiPathCliToolTests.cs`

**Interfaces:**
- Consumes: `CliCommandPolicy` / `CliCommandClass` and `UiPathCliResult.StdOut`/`StdErr` (Task 3); `FakeUiPathCliProvider` and `FakeFilesystemProvider` (existing `Fakes.cs`); `ToolResults.GuardAllowedPath` (existing).
- Produces: `ToolErrorCodes.CliVerbNotAllowed`, `.MutatingCommandDisabled`; registered `ISkillsProvider` and `CliCommandPolicy` in DI.

- [ ] **Step 1: Add the error codes**

In `src/UiPath.Engineering.Mcp.Core/ToolErrorCodes.cs`, append inside the class:

```csharp
    public const string CliVerbNotAllowed = "CLI_VERB_NOT_ALLOWED";
    public const string MutatingCommandDisabled = "MUTATING_COMMAND_DISABLED";
```

- [ ] **Step 2: Write the failing tests**

Create `tests/UiPath.Engineering.Mcp.Tools.Tests/RunUiPathCliToolTests.cs`:

```csharp
using Microsoft.Extensions.Options;
using UiPath.Engineering.Mcp.Core.Configuration;
using UiPath.Engineering.Mcp.Providers.UiPathCli;
using UiPath.Engineering.Mcp.Tools;

namespace UiPath.Engineering.Mcp.Tools.Tests;

public class RunUiPathCliToolTests {
    private static RunUiPathCliTool CreateSut(
        FakeUiPathCliProvider cli, FakeFilesystemProvider filesystem, Action<UiPathCliOptions>? configure = null) {
        var options = new UiPathCliOptions();
        configure?.Invoke(options);
        return new RunUiPathCliTool(cli, filesystem, new CliCommandPolicy(options), Options.Create(options));
    }

    [Fact]
    public async Task ReadOnlyCommand_ExecutesAndReturnsStructuredOutput() {
        var cli = new FakeUiPathCliProvider {
            RunResult = new UiPathCliResult {
                Success = true, Command = "uip rpa validate", ExitCode = 0,
                Summary = "'rpa' completed.", StdOut = "all good"
            }
        };
        var sut = CreateSut(cli, new FakeFilesystemProvider());

        var result = await sut.RunUiPathCli("rpa", "validate --project-dir \"C:/proj\" --output json");

        Assert.Equal("success", result.Status);
        Assert.Equal("rpa", cli.LastVerb);
        Assert.Equal("validate --project-dir \"C:/proj\" --output json", cli.LastArguments);
    }

    [Fact]
    public async Task VerbOutsideAllowlist_ReturnsStructuredError_AndNeverRuns() {
        var cli = new FakeUiPathCliProvider();
        var sut = CreateSut(cli, new FakeFilesystemProvider());

        var result = await sut.RunUiPathCli("orx", "assets list");

        Assert.Equal("error", result.Status);
        Assert.Contains(result.ErrorDetails, e => e.ErrorCode == "CLI_VERB_NOT_ALLOWED");
        Assert.Null(cli.LastVerb);
    }

    [Fact]
    public async Task MutatingCommand_WhenDisabled_IsRefused_AndNeverRuns() {
        var cli = new FakeUiPathCliProvider();
        var sut = CreateSut(cli, new FakeFilesystemProvider());

        var result = await sut.RunUiPathCli("solution", "publish --output json");

        Assert.Equal("error", result.Status);
        Assert.Contains(result.ErrorDetails, e => e.ErrorCode == "MUTATING_COMMAND_DISABLED");
        Assert.Null(cli.LastVerb);
    }

    [Fact]
    public async Task MutatingCommand_WhenEnabled_Executes() {
        var cli = new FakeUiPathCliProvider {
            RunResult = new UiPathCliResult { Success = true, Summary = "'solution' completed." }
        };
        var sut = CreateSut(cli, new FakeFilesystemProvider(), o => o.EnableMutatingCommands = true);

        var result = await sut.RunUiPathCli("solution", "pack");

        Assert.Equal("success", result.Status);
        Assert.Equal("solution", cli.LastVerb);
    }

    [Fact]
    public async Task UnknownSubcommand_FailsClosed_AsMutating() {
        var cli = new FakeUiPathCliProvider();
        var sut = CreateSut(cli, new FakeFilesystemProvider());

        var result = await sut.RunUiPathCli("rpa", "brand-new-subcommand");

        Assert.Equal("error", result.Status);
        Assert.Contains(result.ErrorDetails, e => e.ErrorCode == "MUTATING_COMMAND_DISABLED");
    }

    [Fact]
    public async Task WorkingDirectoryOutsideAllowedRoots_IsRejected() {
        var cli = new FakeUiPathCliProvider();
        var fs = new FakeFilesystemProvider { Allowed = false };
        var sut = CreateSut(cli, fs);

        var result = await sut.RunUiPathCli("rpa", "validate --project-dir x", workingDirectory: "C:/elsewhere");

        Assert.Equal("error", result.Status);
        Assert.Null(cli.LastVerb);
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test tests/UiPath.Engineering.Mcp.Tools.Tests --filter "FullyQualifiedName~RunUiPathCliToolTests"`
Expected: build failure — `RunUiPathCliTool` does not exist.

- [ ] **Step 4: Write the tool**

Create `src/UiPath.Engineering.Mcp.Tools/RunUiPathCliTool.cs`:

```csharp
using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using UiPath.Engineering.Mcp.Core;
using UiPath.Engineering.Mcp.Core.Abstractions;
using UiPath.Engineering.Mcp.Core.Configuration;
using UiPath.Engineering.Mcp.Core.Models;
using UiPath.Engineering.Mcp.Providers.UiPathCli;

namespace UiPath.Engineering.Mcp.Tools;

[McpServerToolType]
public sealed class RunUiPathCliTool {
    private readonly IUiPathCliProvider _cli;
    private readonly IFilesystemProvider _filesystem;
    private readonly CliCommandPolicy _policy;
    private readonly UiPathCliOptions _options;

    public RunUiPathCliTool(
        IUiPathCliProvider cli,
        IFilesystemProvider filesystem,
        CliCommandPolicy policy,
        IOptions<UiPathCliOptions> options) {
        _cli = cli;
        _filesystem = filesystem;
        _policy = policy;
        _options = options.Value;
    }

    [McpServerTool, Description("Runs an allowlisted UiPath CLI (uip) command and returns structured output. Allowed verbs are configured server-side (default: rpa, solution); mutating subcommands are blocked unless enabled in server config. stdout/stderr are redacted and capped.")]
    public async Task<ToolResult> RunUiPathCli(
        [Description("Top-level uip verb, e.g. 'rpa' or 'solution'.")] string verb,
        [Description("Arguments appended verbatim after the verb, e.g. 'validate --project-dir \"C:/proj\" --output json'.")] string arguments,
        [Description("Optional working directory; must be inside an allowed project root.")] string? workingDirectory = null,
        CancellationToken cancellationToken = default) {

        var sw = Stopwatch.StartNew();

        if (string.IsNullOrWhiteSpace(verb)) {
            return ToolResults.Failure("verb is required.", sw);
        }
        if (string.IsNullOrWhiteSpace(arguments)) {
            return ToolResults.Failure("arguments is required.", sw);
        }

        var classification = _policy.Classify(verb, arguments);
        if (classification == CliCommandClass.VerbNotAllowed) {
            return ToolResults.Failure($"Verb '{verb}' is not allowed.",
                [new ToolError(ToolErrorCodes.CliVerbNotAllowed,
                    $"The verb '{verb}' is not in the server allowlist.",
                    $"Use one of: {string.Join(", ", _options.AllowedVerbs)}.")], sw);
        }

        if (classification == CliCommandClass.AllowedMutating && !_options.EnableMutatingCommands) {
            var subcommand = arguments.TrimStart().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            return ToolResults.Failure("Mutating command blocked.",
                [new ToolError(ToolErrorCodes.MutatingCommandDisabled,
                    $"'{verb} {subcommand}' is classified as mutating and mutating commands are disabled on this server.",
                    "Set UiPathCli:EnableMutatingCommands to true in appsettings.json and restart the server.")], sw);
        }

        if (workingDirectory is not null
            && ToolResults.GuardAllowedPath(_filesystem, workingDirectory, sw) is { } guardFailure) {
            return guardFailure;
        }

        var result = await _cli.RunAsync(verb, arguments, workingDirectory, cancellationToken);

        return new ToolResult {
            Status = result.Success ? "success" : "error",
            Summary = result.Summary,
            Data = new {
                command = result.Command,
                exitCode = result.ExitCode,
                success = result.Success,
                stdout = result.StdOut,
                stderr = result.StdErr,
                errors = result.Errors,
                warnings = result.Warnings
            },
            Errors = result.Errors,
            Warnings = result.Warnings,
            DurationMs = sw.ElapsedMilliseconds
        };
    }
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/UiPath.Engineering.Mcp.Tools.Tests --filter "FullyQualifiedName~RunUiPathCliToolTests"`
Expected: 6 tests PASS.

- [ ] **Step 6: Register in DI**

In `src/UiPath.Engineering.Mcp.Server/Program.cs`:

Add to the usings:

```csharp
using Microsoft.Extensions.Options;
using UiPath.Engineering.Mcp.Providers.Skills;
```

Add after the existing `builder.Services.Configure<UiPathCliOptions>(...)` line:

```csharp
builder.Services.Configure<SkillsOptions>(builder.Configuration.GetSection("Skills"));
```

Add after the existing `builder.Services.AddSingleton<IUiPathCliProvider, UiPathCliProvider>();` line:

```csharp
builder.Services.AddSingleton<ISkillsProvider, SkillsProvider>();
builder.Services.AddSingleton(sp =>
    new CliCommandPolicy(sp.GetRequiredService<IOptions<UiPathCliOptions>>().Value));
```

- [ ] **Step 7: Add appsettings sections**

In `src/UiPath.Engineering.Mcp.Server/appsettings.json`, add a `Skills` section and extend `UiPathCli`:

```json
  "Skills": {
    "SkillsRoot": "C:/Users/arauj/Documents/UiPathEngineeringMCP/.agents/skills",
    "MaxSkillFileBytes": 65536
  },
  "UiPathCli": {
    "ExecutablePath": "uip",
    "DefaultTimeoutSeconds": 300,
    "IncludeRawOutput": false,
    "AllowedVerbs": ["rpa", "solution"],
    "ReadOnlySubcommands": {
      "rpa": ["analyze", "validate", "build"],
      "solution": ["list", "status"]
    },
    "EnableMutatingCommands": false,
    "MaxOutputChars": 32768
  },
```

(`SkillsRoot` is absolute because the server's working directory when run via `scripts/run-local.ps1` is the Server project directory, not the repo root. Adjust if the checkout moves.)

- [ ] **Step 8: Full solution verification**

Run: `dotnet build UiPath.Engineering.Mcp.sln && dotnet test UiPath.Engineering.Mcp.sln`
Expected: build succeeds, all tests PASS (new and existing).

Optional smoke test (requires `uip` on PATH and the dev server running):
`list_skills` returns ~25 skills; `read_skill("uipath-rpa")` returns the playbook; `run_uip_cli("rpa", "validate --project-dir \"<testProcess path>\" --output json")` returns structured output; `run_uip_cli("solution", "publish")` returns `MUTATING_COMMAND_DISABLED`.

- [ ] **Step 9: Commit**

```bash
git add src/UiPath.Engineering.Mcp.Core/ToolErrorCodes.cs src/UiPath.Engineering.Mcp.Tools/RunUiPathCliTool.cs src/UiPath.Engineering.Mcp.Server/Program.cs src/UiPath.Engineering.Mcp.Server/appsettings.json tests/UiPath.Engineering.Mcp.Tools.Tests/RunUiPathCliToolTests.cs
git commit -m "feat: add run_uip_cli tool with allowlisted verb policy"
```
