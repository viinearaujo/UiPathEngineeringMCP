# SP1: C# Intelligence (Roslyn) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add Roslyn-based C# semantic intelligence to the UiPath Engineering MCP server: `find_code_symbol`, `find_code_references`, `get_code_context`, `get_compile_errors`, and `compile_project` tools backed by a cached in-process `CSharpCompilation` per project.

**Architecture:** A new `CodeAnalysis/` folder in `UiPath.Engineering.Mcp.Core` builds and caches a Roslyn `CSharpCompilation` per UiPath project (fingerprint-invalidated, mirroring `CachingProjectModelBuilder`). `NuGetReferenceResolver` maps `project.json` dependencies to assemblies in the NuGet global-packages folder plus framework targeting packs. Five thin tool classes in `UiPath.Engineering.Mcp.Tools` expose the queries; `compile_project` reuses the existing `IUiPathCliProvider`. When references cannot be resolved the server degrades gracefully (`full` / `partial` / `syntaxOnly` modes) instead of failing.

**Tech Stack:** .NET 8, C# 12, `Microsoft.CodeAnalysis.CSharp` 4.8.0 (only new dependency — no Workspaces package), xUnit with hand-written fakes (no Moq).

**Spec:** `docs/superpowers/specs/2026-08-10-csharp-intelligence-design.md`

## Global Constraints

- .NET 8, C# only. No new dependencies beyond `Microsoft.CodeAnalysis.CSharp` 4.8.0 on `UiPath.Engineering.Mcp.Core`.
- Tools never throw raw exceptions to the MCP client; use `ToolResults.Ok` / `ToolResults.Failure` / `ToolResults.FromException` and the standard `ToolResult` envelope.
- All tools guard paths with `ToolResults.GuardProject(_filesystem, projectPath, sw)`.
- DTO properties are PascalCase (matches `UiPathProjectModel` serialization style).
- `analysisMode` values are exactly `"full"`, `"partial"`, `"syntaxOnly"`.
- Tests: xUnit, hand-written fakes, no Moq. Core tests use the in-memory `FakeFilesystemProvider` (`tests/UiPath.Engineering.Mcp.Core.Tests/FakeFilesystemProvider.cs`).
- Do NOT modify `CodedSourceFileParser`, `analyze_project`, or any existing tool behavior. The only permitted touches to existing files are: `UiPathProjectModel` (add one property), `ProjectJsonParser` (parse one property), Core `.csproj` (add package), `Program.cs` (DI), `README.md` (docs).
- Commit after every task; `git add` ONLY the files listed in that task (the working tree has unrelated pending deletions — never `git add -A` or `git add .`).
- Test commands run from the repo root: `C:/Users/arauj/Documents/UiPathEngineeringMCP`.

---

### Task 1: Roslyn package + `targetFramework` parsing

**Files:**
- Modify: `src/UiPath.Engineering.Mcp.Core/UiPath.Engineering.Mcp.Core.csproj`
- Modify: `src/UiPath.Engineering.Mcp.Core/Models/UiPathProjectModel.cs`
- Modify: `src/UiPath.Engineering.Mcp.Core/Models/Parsing/ProjectJsonParser.cs`
- Test: `tests/UiPath.Engineering.Mcp.Core.Tests/ProjectJsonParserTests.cs` (append one test)

**Interfaces:**
- Produces: `UiPathProjectModel.TargetFramework` (`string?`) consumed by `CSharpContextBuilder` in Task 3.

- [ ] **Step 1: Write the failing test**

Append to `tests/UiPath.Engineering.Mcp.Core.Tests/ProjectJsonParserTests.cs` (the file's existing constants are `ProjectRoot` and `ProjectJsonPath`):

```csharp
[Fact]
public void Parse_TargetFramework_IsCaptured() {
    var fs = new FakeFilesystemProvider { ProjectJsonPath = ProjectJsonPath };
    fs.FileContents[ProjectJsonPath] = """
        {
          "name": "testProcess",
          "targetFramework": "net6.0",
          "dependencies": { "UiPath.System.Activities": "24.10.4" }
        }
        """;
    var parser = new ProjectJsonParser(fs);

    var model = parser.Parse(ProjectJsonPath, ProjectRoot);

    Assert.Equal("net6.0", model.TargetFramework);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/UiPath.Engineering.Mcp.Core.Tests --filter "FullyQualifiedName~Parse_TargetFramework_IsCaptured"`
Expected: FAIL — `UiPathProjectModel` does not contain a definition for `TargetFramework` (compile error).

- [ ] **Step 3: Add the property and parse it**

In `src/UiPath.Engineering.Mcp.Core/Models/UiPathProjectModel.cs`, add after `Description`:

```csharp
public string? TargetFramework { get; init; }
```

In `src/UiPath.Engineering.Mcp.Core/Models/Parsing/ProjectJsonParser.cs`, add to the returned `UiPathProjectModel` initializer (after the `Description = ...` line):

```csharp
TargetFramework = root.TryGetProperty("targetFramework", out var tf) ? tf.GetString() : null,
```

- [ ] **Step 4: Add the Roslyn package**

Run: `dotnet add src/UiPath.Engineering.Mcp.Core package Microsoft.CodeAnalysis.CSharp --version 4.8.0`

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test tests/UiPath.Engineering.Mcp.Core.Tests --filter "FullyQualifiedName~ProjectJsonParserTests"`
Expected: PASS (all parser tests).

- [ ] **Step 6: Commit**

```bash
git add src/UiPath.Engineering.Mcp.Core/UiPath.Engineering.Mcp.Core.csproj src/UiPath.Engineering.Mcp.Core/Models/UiPathProjectModel.cs src/UiPath.Engineering.Mcp.Core/Models/Parsing/ProjectJsonParser.cs tests/UiPath.Engineering.Mcp.Core.Tests/ProjectJsonParserTests.cs
git commit -m "feat: parse targetFramework from project.json; add Roslyn package"
```

---

### Task 2: `NuGetReferenceResolver`

**Files:**
- Create: `src/UiPath.Engineering.Mcp.Core/CodeAnalysis/ReferenceResolution.cs`
- Create: `src/UiPath.Engineering.Mcp.Core/CodeAnalysis/NuGetReferenceResolver.cs`
- Test: `tests/UiPath.Engineering.Mcp.Core.Tests/NuGetReferenceResolverTests.cs`

**Interfaces:**
- Consumes: `PackageModel` (`Id`, `Version`) from `UiPath.Engineering.Mcp.Core.Models`.
- Produces: `NuGetReferenceResolver.GetPackagesFolder()` (`string?`), `NuGetReferenceResolver.Resolve(IReadOnlyList<PackageModel>, string? targetFramework)` → `ReferenceResolution` with `AssemblyPaths` (`List<string>`), `UnresolvedDependencies` (`List<string>`), `FrameworkResolved` (`bool`), `PackagesFolderFound` (`bool`). Consumed by `CSharpContextBuilder` in Task 3.

- [ ] **Step 1: Write the failing tests**

Create `tests/UiPath.Engineering.Mcp.Core.Tests/NuGetReferenceResolverTests.cs`:

```csharp
using UiPath.Engineering.Mcp.Core.CodeAnalysis;
using UiPath.Engineering.Mcp.Core.Models;

namespace UiPath.Engineering.Mcp.Core.Tests;

public class NuGetReferenceResolverTests : IDisposable {
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), "nuget-resolver-tests-" + Guid.NewGuid().ToString("N"));

    public NuGetReferenceResolverTests() => Directory.CreateDirectory(_tempRoot);

    public void Dispose() {
        try { Directory.Delete(_tempRoot, recursive: true); } catch (IOException) { }
    }

    private string CreatePackage(string id, string version, string group, string tfm) {
        var dir = Path.Combine(_tempRoot, id.ToLowerInvariant(), version, group, tfm);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, id + ".dll"), "placeholder");
        return dir;
    }

    [Fact]
    public void GetPackagesFolder_OverrideMissing_ReturnsNull() {
        var resolver = new NuGetReferenceResolver(Path.Combine(_tempRoot, "does-not-exist"));
        Assert.Null(resolver.GetPackagesFolder());
    }

    [Fact]
    public void Resolve_PackagesFolderMissing_AllDependenciesUnresolved() {
        var resolver = new NuGetReferenceResolver(Path.Combine(_tempRoot, "does-not-exist"));
        var packages = new List<PackageModel> { new() { Id = "UiPath.System.Activities", Version = "24.10.4" } };

        var result = resolver.Resolve(packages, "net6.0");

        Assert.False(result.PackagesFolderFound);
        Assert.Equal(["UiPath.System.Activities"], result.UnresolvedDependencies);
    }

    [Fact]
    public void Resolve_ExactVersionAndExactTfm_SelectsRefAssemblies() {
        CreatePackage("UiPath.System.Activities", "24.10.4", "ref", "net6.0");
        var resolver = new NuGetReferenceResolver(_tempRoot);
        var packages = new List<PackageModel> { new() { Id = "UiPath.System.Activities", Version = "24.10.4" } };

        var result = resolver.Resolve(packages, "net6.0");

        Assert.Empty(result.UnresolvedDependencies);
        Assert.Contains(result.AssemblyPaths, p => p.Contains(Path.Combine("ref", "net6.0")));
    }

    [Fact]
    public void Resolve_VersionMissing_FallsBackToHighestInstalled() {
        CreatePackage("UiPath.System.Activities", "25.0.0", "lib", "net6.0");
        var resolver = new NuGetReferenceResolver(_tempRoot);
        var packages = new List<PackageModel> { new() { Id = "UiPath.System.Activities", Version = "24.10.4" } };

        var result = resolver.Resolve(packages, "net6.0");

        Assert.Empty(result.UnresolvedDependencies);
        Assert.Contains(result.AssemblyPaths, p => p.Contains("25.0.0"));
    }

    [Fact]
    public void Resolve_PackageFolderMissing_DependencyUnresolved() {
        var resolver = new NuGetReferenceResolver(_tempRoot);
        var packages = new List<PackageModel> { new() { Id = "Not.Installed", Version = "1.0.0" } };

        var result = resolver.Resolve(packages, "net6.0");

        Assert.Equal(["Not.Installed"], result.UnresolvedDependencies);
    }

    [Theory]
    [InlineData("net6.0", "net6.0", 1000)] // exact match wins
    [InlineData("netstandard2.0", "net6.0", 20)] // netstandard ranks below net folders
    [InlineData("net8.0", "net6.0", -1)] // newer than target is incompatible
    [InlineData("net5.0", "net6.0", 50)] // older net folder compatible
    [InlineData("net461", "net472", 461)] // legacy: lower version compatible
    [InlineData("netstandard2.0", "net472", -1)] // legacy target: no netstandard
    public void ScoreTfmFolder_CompatibilityMatrix(string folder, string target, int expected) {
        Assert.Equal(expected, NuGetReferenceResolver.ScoreTfmFolder(folder, target));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/UiPath.Engineering.Mcp.Core.Tests --filter "FullyQualifiedName~NuGetReferenceResolverTests"`
Expected: FAIL — type `NuGetReferenceResolver` does not exist (compile error).

- [ ] **Step 3: Implement `ReferenceResolution` and `NuGetReferenceResolver`**

Create `src/UiPath.Engineering.Mcp.Core/CodeAnalysis/ReferenceResolution.cs`:

```csharp
namespace UiPath.Engineering.Mcp.Core.CodeAnalysis;

/// <summary>
/// Result of resolving a UiPath project's compilation references: assembly file
/// paths plus bookkeeping about what could not be resolved. Pure path selection —
/// no assembly is loaded at this stage.
/// </summary>
public sealed class ReferenceResolution {
    public List<string> AssemblyPaths { get; set; } = [];
    public List<string> UnresolvedDependencies { get; set; } = [];
    public bool FrameworkResolved { get; set; }
    public bool PackagesFolderFound { get; set; }
}
```

Create `src/UiPath.Engineering.Mcp.Core/CodeAnalysis/NuGetReferenceResolver.cs`:

```csharp
using System.Text.RegularExpressions;
using UiPath.Engineering.Mcp.Core.Models;

namespace UiPath.Engineering.Mcp.Core.CodeAnalysis;

/// <summary>
/// Maps project.json dependencies to assembly file paths in the NuGet global-packages
/// folder, plus framework reference assemblies from the machine's .NET targeting packs
/// (falling back to the server's own runtime assemblies for modern targets).
/// </summary>
public sealed class NuGetReferenceResolver {
    private readonly string? _packagesFolderOverride;

    // packagesFolderOverride exists for tests; production uses the default probing.
    public NuGetReferenceResolver(string? packagesFolderOverride = null) {
        _packagesFolderOverride = packagesFolderOverride;
    }

    public string? GetPackagesFolder() {
        var folder = _packagesFolderOverride
            ?? Environment.GetEnvironmentVariable("NUGET_PACKAGES")
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".nuget", "packages");
        return Directory.Exists(folder) ? folder : null;
    }

    public ReferenceResolution Resolve(IReadOnlyList<PackageModel> packages, string? targetFramework) {
        var result = new ReferenceResolution();
        var paths = new List<string>();

        var (frameworkPaths, frameworkResolved) = ResolveFrameworkAssemblies(targetFramework);
        paths.AddRange(frameworkPaths);
        result.FrameworkResolved = frameworkResolved;

        var packagesFolder = GetPackagesFolder();
        result.PackagesFolderFound = packagesFolder is not null;

        foreach (var package in packages) {
            var packagePaths = packagesFolder is null
                ? []
                : ResolvePackageAssemblies(packagesFolder, package, targetFramework);
            if (packagePaths.Count == 0) {
                result.UnresolvedDependencies.Add(package.Id);
            } else {
                paths.AddRange(packagePaths);
            }
        }

        result.AssemblyPaths = DeduplicateByFileName(paths);
        return result;
    }

    // --- Framework assemblies -------------------------------------------------

    internal static (List<string> Paths, bool Resolved) ResolveFrameworkAssemblies(string? targetFramework) {
        var tfm = NormalizeTfm(targetFramework);
        return IsLegacyTfm(tfm) ? ResolveLegacyFramework(tfm) : ResolveModernFramework(tfm);
    }

    internal static string NormalizeTfm(string? targetFramework) {
        if (string.IsNullOrWhiteSpace(targetFramework)) {
            return "net8.0";
        }
        var tfm = targetFramework.Trim().ToLowerInvariant();
        // UiPath writes values like "net6.0" or "net6.0-windows"; strip platform suffixes.
        var dash = tfm.IndexOf('-');
        return dash > 0 ? tfm[..dash] : tfm;
    }

    private static bool IsLegacyTfm(string tfm) => Regex.IsMatch(tfm, @"^net4\d\d$");

    private static (List<string> Paths, bool Resolved) ResolveModernFramework(string tfm) {
        var packsRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "dotnet", "packs", "Microsoft.NETCore.App.Ref");
        if (Directory.Exists(packsRoot)) {
            var match = Directory.GetDirectories(packsRoot)
                .Select(versionDir => Path.Combine(versionDir, "ref", tfm))
                .Where(Directory.Exists)
                .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            if (match is not null) {
                return (Directory.GetFiles(match, "*.dll").ToList(), true);
            }
        }

        // Fallback: the server's own runtime assemblies are valid compile references.
        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location);
        return runtimeDir is not null
            ? (Directory.GetFiles(runtimeDir, "*.dll").ToList(), true)
            : ([], false);
    }

    private static (List<string> Paths, bool Resolved) ResolveLegacyFramework(string tfm) {
        var version = tfm switch {
            "net48" => "4.8",
            "net472" => "4.7.2",
            "net471" => "4.7.1",
            "net47" => "4.7",
            "net462" => "4.6.2",
            "net461" => "4.6.1",
            "net46" => "4.6",
            "net45" => "4.5",
            _ => null
        };
        if (version is null) {
            return ([], false);
        }

        var refDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Reference Assemblies", "Microsoft", "Framework", ".NETFramework", "v" + version);
        return Directory.Exists(refDir)
            ? (Directory.GetFiles(refDir, "*.dll").ToList(), true)
            : ([], false);
    }

    // --- Package assemblies ---------------------------------------------------

    private static List<string> ResolvePackageAssemblies(string packagesFolder, PackageModel package, string? targetFramework) {
        var idFolder = Path.Combine(packagesFolder, package.Id.ToLowerInvariant());
        if (!Directory.Exists(idFolder)) {
            return [];
        }

        var versionFolder = SelectVersionFolder(idFolder, package.Version);
        if (versionFolder is null) {
            return [];
        }

        var tfm = NormalizeTfm(targetFramework);
        foreach (var group in new[] { "ref", "lib" }) {
            var groupDir = Path.Combine(versionFolder, group);
            if (!Directory.Exists(groupDir)) {
                continue;
            }
            var bestTfmDir = SelectBestTfmFolder(groupDir, tfm);
            if (bestTfmDir is not null) {
                return Directory.GetFiles(bestTfmDir, "*.dll").ToList();
            }
        }

        return [];
    }

    internal static string? SelectVersionFolder(string idFolder, string wantedVersion) {
        var dirs = Directory.GetDirectories(idFolder);
        var exact = dirs.FirstOrDefault(d =>
            string.Equals(Path.GetFileName(d), wantedVersion, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) {
            return exact;
        }

        return dirs
            .Select(d => (Dir: d, Version: ParseVersion(Path.GetFileName(d))))
            .Where(x => x.Version is not null)
            .OrderByDescending(x => x.Version)
            .Select(x => x.Dir)
            .FirstOrDefault();
    }

    private static Version? ParseVersion(string text) =>
        Version.TryParse(text.Split('-')[0], out var version) ? version : null;

    internal static string? SelectBestTfmFolder(string groupDir, string targetTfm) =>
        Directory.GetDirectories(groupDir)
            .Select(d => (Dir: d, Score: ScoreTfmFolder(Path.GetFileName(d), targetTfm)))
            .Where(x => x.Score >= 0)
            .OrderByDescending(x => x.Score)
            .Select(x => x.Dir)
            .FirstOrDefault();

    /// <summary>
    /// Scores a package's ref/lib TFM folder against the project target.
    /// Higher is better; -1 means incompatible. Exact match = 1000.
    /// For modern targets: older netX.Y folders rank by version, netstandard below them.
    /// For legacy (net4xx) targets: only net4xx folders qualify.
    /// </summary>
    public static int ScoreTfmFolder(string folderName, string targetTfm) {
        var name = folderName.ToLowerInvariant();
        var target = targetTfm.ToLowerInvariant();
        if (name == target) {
            return 1000;
        }

        if (IsLegacyTfm(target)) {
            return IsLegacyTfm(name) && TfmVersionRank(name) <= TfmVersionRank(target)
                ? TfmVersionRank(name)
                : -1;
        }

        if (Regex.IsMatch(name, @"^net\d+\.\d+$")) {
            var rank = TfmVersionRank(name);
            return rank <= TfmVersionRank(target) ? rank : -1;
        }

        if (Regex.IsMatch(name, @"^netstandard\d+(\.\d+)?$")) {
            // netstandard is usable from net5+ and always ranks below netX.Y folders.
            var rank = TfmVersionRank(name);
            return rank <= 21 ? rank : -1;
        }

        return -1;
    }

    // "net6.0" -> 60, "netstandard2.0" -> 20, "net461" -> 461.
    private static int TfmVersionRank(string tfm) {
        var dotted = Regex.Match(tfm, @"\d+\.\d+");
        if (dotted.Success) {
            var parts = dotted.Value.Split('.');
            return int.Parse(parts[0]) * 10 + int.Parse(parts[1]);
        }
        var plain = Regex.Match(tfm, @"\d+");
        return plain.Success ? int.Parse(plain.Value) : 0;
    }

    // Keeps the first occurrence of each file name (framework paths come first),
    // preventing CS1703 duplicate-reference noise from packages shipping facades.
    private static List<string> DeduplicateByFileName(List<string> paths) {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var path in paths) {
            if (seen.Add(Path.GetFileName(path))) {
                result.Add(path);
            }
        }
        return result;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/UiPath.Engineering.Mcp.Core.Tests --filter "FullyQualifiedName~NuGetReferenceResolverTests"`
Expected: PASS (7 tests).

- [ ] **Step 5: Commit**

```bash
git add src/UiPath.Engineering.Mcp.Core/CodeAnalysis/ReferenceResolution.cs src/UiPath.Engineering.Mcp.Core/CodeAnalysis/NuGetReferenceResolver.cs tests/UiPath.Engineering.Mcp.Core.Tests/NuGetReferenceResolverTests.cs
git commit -m "feat: add NuGetReferenceResolver for project.json dependency assemblies"
```

---

### Task 3: `CSharpAnalysisContext` + `CSharpContextBuilder`

**Files:**
- Create: `src/UiPath.Engineering.Mcp.Core/CodeAnalysis/CSharpAnalysisContext.cs`
- Create: `src/UiPath.Engineering.Mcp.Core/CodeAnalysis/ICSharpContextBuilder.cs`
- Create: `src/UiPath.Engineering.Mcp.Core/CodeAnalysis/CSharpContextBuilder.cs`
- Test: `tests/UiPath.Engineering.Mcp.Core.Tests/CSharpContextBuilderTests.cs`

**Interfaces:**
- Consumes: `IFilesystemProvider`, `ProjectJsonParser` (Task 1), `NuGetReferenceResolver` (Task 2).
- Produces: `ICSharpContextBuilder.BuildAsync(string projectPath, CancellationToken)` → `CSharpAnalysisContext` (`Compilation`, `Mode`, `UnresolvedReferences`, `Warnings`, `HasCSharpFiles`). Consumed by Task 4 (cache) and Task 5 (service).

- [ ] **Step 1: Write the failing tests**

Create `tests/UiPath.Engineering.Mcp.Core.Tests/CSharpContextBuilderTests.cs`:

```csharp
using UiPath.Engineering.Mcp.Core.CodeAnalysis;

namespace UiPath.Engineering.Mcp.Core.Tests;

public class CSharpContextBuilderTests {
    private const string Root = "/projects/testProcess";
    private const string Json = "/projects/testProcess/project.json";
    private const string FlowCs = "/projects/testProcess/InvoiceFlow.cs";

    private const string CodedWorkflowSource = """
        using System;

        namespace TestProcess;

        public class InvoiceFlow {
            public int Execute(string input, int count) {
                return count + 1;
            }
        }
        """;

    private static FakeFilesystemProvider CreateFilesystem(string projectJson) {
        var fs = new FakeFilesystemProvider { ProjectJsonPath = Json };
        fs.FileContents[Json] = projectJson;
        fs.FileContents[FlowCs] = CodedWorkflowSource;
        fs.CSharpFiles.Add(FlowCs);
        fs.WriteTimesUtc[Json] = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        fs.WriteTimesUtc[FlowCs] = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        return fs;
    }

    [Fact]
    public async Task BuildAsync_NoDependencies_FrameworkResolved_FullMode() {
        var fs = CreateFilesystem("""{ "name": "testProcess", "targetFramework": "net8.0", "dependencies": {} }""");
        var sut = new CSharpContextBuilder(fs, new NuGetReferenceResolver("/nonexistent-nuget-folder"));

        var context = await sut.BuildAsync(Root);

        Assert.Equal(CSharpAnalysisMode.Full, context.Mode);
        Assert.True(context.HasCSharpFiles);
        Assert.Empty(context.UnresolvedReferences);
        // The compilation must contain the parsed syntax tree.
        Assert.Contains(context.Compilation.SyntaxTrees, t => string.Equals(t.FilePath, FlowCs, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task BuildAsync_PackagesFolderMissingWithDependencies_SyntaxOnlyMode() {
        var fs = CreateFilesystem("""
            { "name": "testProcess", "targetFramework": "net6.0",
              "dependencies": { "UiPath.System.Activities": "24.10.4" } }
            """);
        var sut = new CSharpContextBuilder(fs, new NuGetReferenceResolver("/nonexistent-nuget-folder"));

        var context = await sut.BuildAsync(Root);

        Assert.Equal(CSharpAnalysisMode.SyntaxOnly, context.Mode);
        Assert.Equal(["UiPath.System.Activities"], context.UnresolvedReferences);
    }

    [Fact]
    public async Task BuildAsync_DependencyNotInstalled_PartialMode() {
        var packagesDir = Path.Combine(Path.GetTempPath(), "ctx-builder-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(packagesDir);
        try {
            var fs = CreateFilesystem("""
                { "name": "testProcess", "targetFramework": "net8.0",
                  "dependencies": { "Not.Installed": "1.0.0" } }
                """);
            var sut = new CSharpContextBuilder(fs, new NuGetReferenceResolver(packagesDir));

            var context = await sut.BuildAsync(Root);

            Assert.Equal(CSharpAnalysisMode.Partial, context.Mode);
            Assert.Equal(["Not.Installed"], context.UnresolvedReferences);
        } finally {
            Directory.Delete(packagesDir, recursive: true);
        }
    }

    [Fact]
    public async Task BuildAsync_NoCSharpFiles_ReportsHasCSharpFilesFalse() {
        var fs = CreateFilesystem("""{ "name": "testProcess", "dependencies": {} }""");
        fs.CSharpFiles.Clear();
        var sut = new CSharpContextBuilder(fs, new NuGetReferenceResolver("/nonexistent-nuget-folder"));

        var context = await sut.BuildAsync(Root);

        Assert.False(context.HasCSharpFiles);
    }

    [Fact]
    public async Task BuildAsync_ProjectJsonMissing_ThrowsFileNotFound() {
        var fs = CreateFilesystem("""{ "name": "testProcess", "dependencies": {} }""");
        fs.ProjectJsonPath = null;
        var sut = new CSharpContextBuilder(fs, new NuGetReferenceResolver("/nonexistent-nuget-folder"));

        await Assert.ThrowsAsync<FileNotFoundException>(() => sut.BuildAsync(Root));
    }
}
```

Note: `new NuGetReferenceResolver("/nonexistent-nuget-folder")` — a path that must not exist on the machine. The Full-mode test works because with zero dependencies the packages folder is never consulted for mode selection (mode rule: `packages.Count > 0 && !PackagesFolderFound` → syntaxOnly).

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/UiPath.Engineering.Mcp.Core.Tests --filter "FullyQualifiedName~CSharpContextBuilderTests"`
Expected: FAIL — types do not exist (compile error).

- [ ] **Step 3: Implement the context, interface, and builder**

Create `src/UiPath.Engineering.Mcp.Core/CodeAnalysis/CSharpAnalysisContext.cs`:

```csharp
using Microsoft.CodeAnalysis.CSharp;

namespace UiPath.Engineering.Mcp.Core.CodeAnalysis;

public enum CSharpAnalysisMode { Full, Partial, SyntaxOnly }

/// <summary>
/// A fully-built Roslyn compilation for one UiPath project, plus resolution
/// bookkeeping that tells callers how much they can trust semantic results.
/// Instances are immutable and safe to share across concurrent tool calls.
/// </summary>
public sealed class CSharpAnalysisContext {
    public required CSharpCompilation Compilation { get; init; }
    public required CSharpAnalysisMode Mode { get; init; }
    public IReadOnlyList<string> UnresolvedReferences { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public bool HasCSharpFiles { get; init; }
}
```

Create `src/UiPath.Engineering.Mcp.Core/CodeAnalysis/ICSharpContextBuilder.cs`:

```csharp
namespace UiPath.Engineering.Mcp.Core.CodeAnalysis;

public interface ICSharpContextBuilder {
    Task<CSharpAnalysisContext> BuildAsync(string projectPath, CancellationToken cancellationToken = default);
}
```

Create `src/UiPath.Engineering.Mcp.Core/CodeAnalysis/CSharpContextBuilder.cs`:

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using UiPath.Engineering.Mcp.Core.Abstractions;
using UiPath.Engineering.Mcp.Core.Parsing;

namespace UiPath.Engineering.Mcp.Core.CodeAnalysis;

/// <summary>
/// Builds a <see cref="CSharpAnalysisContext"/> for a UiPath project: parses every
/// .cs file, resolves references from project.json via <see cref="NuGetReferenceResolver"/>,
/// and assembles the <see cref="CSharpCompilation"/>. Unreadable files and unloadable
/// assemblies are skipped with warnings instead of failing the whole build.
/// </summary>
public sealed class CSharpContextBuilder : ICSharpContextBuilder {
    private readonly IFilesystemProvider _filesystem;
    private readonly NuGetReferenceResolver _resolver;

    public CSharpContextBuilder(IFilesystemProvider filesystem, NuGetReferenceResolver resolver) {
        _filesystem = filesystem;
        _resolver = resolver;
    }

    public Task<CSharpAnalysisContext> BuildAsync(string projectPath, CancellationToken cancellationToken = default) {
        var projectJsonPath = _filesystem.FindProjectJson(projectPath)
            ?? throw new FileNotFoundException("project.json not found.", Path.Combine(projectPath, "project.json"));
        var model = new ProjectJsonParser(_filesystem).Parse(projectJsonPath, projectPath);

        var warnings = new List<string>();
        var parseOptions = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest);
        var csFiles = _filesystem.FindCSharpFiles(projectPath);
        var trees = new List<SyntaxTree>();
        foreach (var file in csFiles) {
            cancellationToken.ThrowIfCancellationRequested();
            try {
                var text = _filesystem.ReadAllText(file);
                trees.Add(CSharpSyntaxTree.ParseText(text, parseOptions, path: file, cancellationToken: cancellationToken));
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FileNotFoundException) {
                warnings.Add($"Skipped unreadable C# file '{file}': {ex.Message}");
            }
        }

        var resolution = _resolver.Resolve(model.Packages, model.TargetFramework);
        var references = new List<MetadataReference>();
        foreach (var path in resolution.AssemblyPaths) {
            try {
                references.Add(MetadataReference.CreateFromFile(path));
            } catch (Exception ex) when (ex is IOException or BadImageFormatException or FileNotFoundException) {
                warnings.Add($"Skipped unloadable assembly '{path}': {ex.Message}");
            }
        }

        var compilation = CSharpCompilation.Create(
            assemblyName: $"analysis-{model.ProjectName}",
            syntaxTrees: trees,
            references: references,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var mode = model.Packages.Count > 0 && !resolution.PackagesFolderFound
            ? CSharpAnalysisMode.SyntaxOnly
            : resolution.UnresolvedDependencies.Count > 0 || !resolution.FrameworkResolved
                ? CSharpAnalysisMode.Partial
                : CSharpAnalysisMode.Full;

        return Task.FromResult(new CSharpAnalysisContext {
            Compilation = compilation,
            Mode = mode,
            UnresolvedReferences = resolution.UnresolvedDependencies,
            Warnings = warnings,
            HasCSharpFiles = csFiles.Count > 0
        });
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/UiPath.Engineering.Mcp.Core.Tests --filter "FullyQualifiedName~CSharpContextBuilderTests"`
Expected: PASS (5 tests).

- [ ] **Step 5: Commit**

```bash
git add src/UiPath.Engineering.Mcp.Core/CodeAnalysis/CSharpAnalysisContext.cs src/UiPath.Engineering.Mcp.Core/CodeAnalysis/ICSharpContextBuilder.cs src/UiPath.Engineering.Mcp.Core/CodeAnalysis/CSharpContextBuilder.cs tests/UiPath.Engineering.Mcp.Core.Tests/CSharpContextBuilderTests.cs
git commit -m "feat: add CSharpContextBuilder with full/partial/syntaxOnly modes"
```

---

### Task 4: `CSharpAnalysisCache` (fingerprint decorator)

**Files:**
- Create: `src/UiPath.Engineering.Mcp.Core/CodeAnalysis/CSharpAnalysisCache.cs`
- Test: `tests/UiPath.Engineering.Mcp.Core.Tests/CSharpAnalysisCacheTests.cs`

**Interfaces:**
- Consumes: `ICSharpContextBuilder` (inner), `IFilesystemProvider`.
- Produces: `CSharpAnalysisCache : ICSharpContextBuilder` — drop-in cached decorator, registered in DI in Task 12. Mirrors `CachingProjectModelBuilder` semantics: fingerprint = count of (`.cs` files + `project.json`) plus newest write ticks; on fingerprint failure serves stale cache, else builds uncached.

- [ ] **Step 1: Write the failing tests**

Create `tests/UiPath.Engineering.Mcp.Core.Tests/CSharpAnalysisCacheTests.cs`:

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using UiPath.Engineering.Mcp.Core.CodeAnalysis;

namespace UiPath.Engineering.Mcp.Core.Tests;

public class CSharpAnalysisCacheTests {
    private const string Root = "/projects/testProcess";
    private const string Json = "/projects/testProcess/project.json";
    private const string FlowCs = "/projects/testProcess/InvoiceFlow.cs";

    private static int _buildCounter;

    private sealed class CountingContextBuilder : ICSharpContextBuilder {
        public int CallCount { get; private set; }
        public Exception? ToThrow { get; set; }

        public Task<CSharpAnalysisContext> BuildAsync(string projectPath, CancellationToken cancellationToken = default) {
            CallCount++;
            if (ToThrow is not null) {
                return Task.FromException<CSharpAnalysisContext>(ToThrow);
            }
            var compilation = CSharpCompilation.Create(
                $"analysis-build-{++_buildCounter}",
                options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            return Task.FromResult(new CSharpAnalysisContext {
                Compilation = compilation,
                Mode = CSharpAnalysisMode.Full,
                HasCSharpFiles = true
            });
        }
    }

    private static FakeFilesystemProvider CreateFilesystem() {
        var fs = new FakeFilesystemProvider { ProjectJsonPath = Json };
        fs.CSharpFiles.Add(FlowCs);
        fs.WriteTimesUtc[Json] = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        fs.WriteTimesUtc[FlowCs] = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        return fs;
    }

    [Fact]
    public async Task BuildAsync_UnchangedFiles_ReturnsCachedContextAndBuildsOnce() {
        var fs = CreateFilesystem();
        var inner = new CountingContextBuilder();
        var sut = new CSharpAnalysisCache(inner, fs);

        var first = await sut.BuildAsync(Root);
        var second = await sut.BuildAsync(Root);

        Assert.Equal(1, inner.CallCount);
        Assert.Same(first, second);
    }

    [Fact]
    public async Task BuildAsync_ChangedCSharpTimestamp_TriggersRebuild() {
        var fs = CreateFilesystem();
        var inner = new CountingContextBuilder();
        var sut = new CSharpAnalysisCache(inner, fs);

        await sut.BuildAsync(Root);
        fs.WriteTimesUtc[FlowCs] = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var second = await sut.BuildAsync(Root);

        Assert.Equal(2, inner.CallCount);
        Assert.NotSame(await sut.BuildAsync("/projects/other"), second);
    }

    [Fact]
    public async Task BuildAsync_AddedCSharpFile_TriggersRebuild() {
        var fs = CreateFilesystem();
        var inner = new CountingContextBuilder();
        var sut = new CSharpAnalysisCache(inner, fs);

        await sut.BuildAsync(Root);
        const string helper = "/projects/testProcess/Helpers.cs";
        fs.CSharpFiles.Add(helper);
        fs.WriteTimesUtc[helper] = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        await sut.BuildAsync(Root);

        Assert.Equal(2, inner.CallCount);
    }

    [Fact]
    public async Task BuildAsync_InnerThrows_ExceptionIsNotCached() {
        var fs = CreateFilesystem();
        var inner = new CountingContextBuilder { ToThrow = new FileNotFoundException("boom") };
        var sut = new CSharpAnalysisCache(inner, fs);

        await Assert.ThrowsAsync<FileNotFoundException>(() => sut.BuildAsync(Root));

        inner.ToThrow = null;
        var context = await sut.BuildAsync(Root);

        Assert.Equal(2, inner.CallCount);
        Assert.NotNull(context);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/UiPath.Engineering.Mcp.Core.Tests --filter "FullyQualifiedName~CSharpAnalysisCacheTests"`
Expected: FAIL — `CSharpAnalysisCache` does not exist (compile error).

- [ ] **Step 3: Implement the cache**

Create `src/UiPath.Engineering.Mcp.Core/CodeAnalysis/CSharpAnalysisCache.cs`:

```csharp
using System.Collections.Concurrent;
using UiPath.Engineering.Mcp.Core.Abstractions;

namespace UiPath.Engineering.Mcp.Core.CodeAnalysis;

/// <summary>
/// Decorates an <see cref="ICSharpContextBuilder"/> with a cross-request cache keyed by
/// the normalized project path. Each call recomputes a cheap fingerprint (count of
/// *.cs files plus project.json, and their newest write time) and only rebuilds the
/// Roslyn compilation when the fingerprint changed. Mirrors CachingProjectModelBuilder.
/// </summary>
public sealed class CSharpAnalysisCache : ICSharpContextBuilder {
    private sealed record CacheEntry(CSharpAnalysisContext Context, long FileCount, long MaxWriteTicks);

    private readonly ICSharpContextBuilder _inner;
    private readonly IFilesystemProvider _filesystem;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.OrdinalIgnoreCase);

    public CSharpAnalysisCache(ICSharpContextBuilder inner, IFilesystemProvider filesystem) {
        _inner = inner;
        _filesystem = filesystem;
    }

    public async Task<CSharpAnalysisContext> BuildAsync(string projectPath, CancellationToken cancellationToken = default) {
        var key = Path.GetFullPath(projectPath);
        var gate = _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));

        await gate.WaitAsync(cancellationToken);
        try {
            if (TryComputeFingerprint(projectPath, out var fileCount, out var maxWriteTicks)) {
                if (_cache.TryGetValue(key, out var entry) &&
                    entry.FileCount == fileCount && entry.MaxWriteTicks == maxWriteTicks) {
                    return entry.Context;
                }

                var built = await _inner.BuildAsync(projectPath, cancellationToken);
                _cache[key] = new CacheEntry(built, fileCount, maxWriteTicks);
                return built;
            }

            // Filesystem inaccessible during fingerprinting: serve stale cache if present,
            // otherwise build directly without caching (we cannot trust a fingerprint).
            if (_cache.TryGetValue(key, out var stale)) {
                return stale.Context;
            }

            return await _inner.BuildAsync(projectPath, cancellationToken);
        } finally {
            gate.Release();
        }
    }

    private bool TryComputeFingerprint(string projectPath, out long fileCount, out long maxWriteTicks) {
        fileCount = 0;
        maxWriteTicks = 0;
        try {
            var files = _filesystem.FindCSharpFiles(projectPath).ToList();
            var projectJson = _filesystem.FindProjectJson(projectPath);
            if (projectJson is not null) {
                files.Add(projectJson);
            }

            foreach (var file in files) {
                var ticks = _filesystem.GetLastWriteTimeUtc(file).Ticks;
                if (ticks > maxWriteTicks) {
                    maxWriteTicks = ticks;
                }
            }

            fileCount = files.Count;
            return true;
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FileNotFoundException or DirectoryNotFoundException) {
            return false;
        }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/UiPath.Engineering.Mcp.Core.Tests --filter "FullyQualifiedName~CSharpAnalysisCacheTests"`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add src/UiPath.Engineering.Mcp.Core/CodeAnalysis/CSharpAnalysisCache.cs tests/UiPath.Engineering.Mcp.Core.Tests/CSharpAnalysisCacheTests.cs
git commit -m "feat: add fingerprint-cached CSharpAnalysisCache decorator"
```

---

### Task 5: Analysis DTOs + `ICSharpAnalysisService` + `FindSymbol`

**Files:**
- Create: `src/UiPath.Engineering.Mcp.Core/CodeAnalysis/CSharpAnalysisDtos.cs`
- Create: `src/UiPath.Engineering.Mcp.Core/CodeAnalysis/ICSharpAnalysisService.cs`
- Create: `src/UiPath.Engineering.Mcp.Core/CodeAnalysis/CSharpAnalysisService.cs` (skeleton + `FindSymbolAsync` only; later tasks append methods)
- Test: `tests/UiPath.Engineering.Mcp.Core.Tests/CSharpAnalysisServiceTestBase.cs` (shared helpers)
- Test: `tests/UiPath.Engineering.Mcp.Core.Tests/FindSymbolTests.cs`

**Interfaces:**
- Consumes: `ICSharpContextBuilder` (Task 3/4).
- Produces: `ICSharpAnalysisService.FindSymbolAsync(string projectPath, string symbol, string? kind = null, CancellationToken)` → `FindSymbolResult`. DTO base `CSharpAnalysisResult` (`AnalysisMode`, `UnresolvedReferences`, `Warnings`, `HasCSharpFiles`, `Note`) reused by Tasks 6-8. `SymbolMatch` (`Name`, `Kind`, `FilePath`, `Line`, `ContainingType`, `Signature`).

- [ ] **Step 1: Write the shared test base and the failing tests**

Create `tests/UiPath.Engineering.Mcp.Core.Tests/CSharpAnalysisServiceTestBase.cs`:

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using UiPath.Engineering.Mcp.Core.CodeAnalysis;

namespace UiPath.Engineering.Mcp.Core.Tests;

/// <summary>
/// Shared helpers for CSharpAnalysisService tests: builds a real Roslyn compilation
/// from source text against the test runtime's assemblies (always resolvable on the
/// build machine) and serves it through a stub ICSharpContextBuilder.
/// </summary>
public abstract class CSharpAnalysisServiceTestBase {
    protected const string Root = "/projects/testProcess";
    protected const string FlowCs = "/projects/testProcess/InvoiceFlow.cs";

    protected static CSharpAnalysisContext BuildContext(
        string source,
        CSharpAnalysisMode mode = CSharpAnalysisMode.Full,
        string filePath = FlowCs,
        bool withRuntimeReferences = true,
        IReadOnlyList<string>? unresolved = null) {
        var tree = CSharpSyntaxTree.ParseText(source, path: filePath);
        List<MetadataReference> references = withRuntimeReferences
            ? Directory.GetFiles(Path.GetDirectoryName(typeof(object).Assembly.Location)!, "*.dll")
                .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
                .ToList()
            : [];
        var compilation = CSharpCompilation.Create(
            "analysis-test",
            [tree],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        return new CSharpAnalysisContext {
            Compilation = compilation,
            Mode = mode,
            UnresolvedReferences = unresolved ?? [],
            HasCSharpFiles = true
        };
    }

    protected static CSharpAnalysisService CreateService(CSharpAnalysisContext context) =>
        new(new StubContextBuilder(context));

    private sealed class StubContextBuilder(CSharpAnalysisContext context) : ICSharpContextBuilder {
        public Task<CSharpAnalysisContext> BuildAsync(string projectPath, CancellationToken cancellationToken = default) =>
            Task.FromResult(context);
    }
}
```

Create `tests/UiPath.Engineering.Mcp.Core.Tests/FindSymbolTests.cs`:

```csharp
using UiPath.Engineering.Mcp.Core.CodeAnalysis;

namespace UiPath.Engineering.Mcp.Core.Tests;

public class FindSymbolTests : CSharpAnalysisServiceTestBase {
    // Line map (1-based): 1 using System; | 2 blank | 3 namespace | 4 blank |
    // 5 class InvoiceFlow | 6 Execute | 7 return | 8 } | 9 blank | 10 Log | 11 Console | 12 } | 13 }
    private const string Source = """
        using System;

        namespace TestProcess;

        public class InvoiceFlow {
            public int Execute(string input, int count) {
                return count + 1;
            }

            private void Log(string message) {
                Console.WriteLine(message);
            }
        }
        """;

    [Fact]
    public async Task FindSymbol_Method_ReturnsMatchWithLocationAndSignature() {
        var service = CreateService(BuildContext(Source));

        var result = await service.FindSymbolAsync(Root, "Execute");

        var match = Assert.Single(result.Matches);
        Assert.Equal("Execute", match.Name);
        Assert.Equal("method", match.Kind);
        Assert.Equal(FlowCs, match.FilePath);
        Assert.Equal(6, match.Line);
        Assert.Equal("TestProcess.InvoiceFlow", match.ContainingType);
        Assert.Contains("Execute", match.Signature);
        Assert.Equal("full", result.AnalysisMode);
    }

    [Fact]
    public async Task FindSymbol_KindFilter_ExcludesNonMatchingKinds() {
        var service = CreateService(BuildContext(Source));

        var methods = await service.FindSymbolAsync(Root, "InvoiceFlow", kind: "method");
        var classes = await service.FindSymbolAsync(Root, "InvoiceFlow", kind: "class");

        Assert.Empty(methods.Matches);
        var match = Assert.Single(classes.Matches);
        Assert.Equal("class", match.Kind);
        Assert.Equal(5, match.Line);
    }

    [Fact]
    public async Task FindSymbol_UnknownName_ReturnsEmptyMatches() {
        var service = CreateService(BuildContext(Source));

        var result = await service.FindSymbolAsync(Root, "DoesNotExist");

        Assert.Empty(result.Matches);
    }

    [Fact]
    public async Task FindSymbol_PartialMode_ReportsModeAndUnresolvedReferences() {
        var context = BuildContext(Source, mode: CSharpAnalysisMode.Partial, unresolved: ["UiPath.System.Activities"]);
        var service = CreateService(context);

        var result = await service.FindSymbolAsync(Root, "Execute");

        Assert.Equal("partial", result.AnalysisMode);
        Assert.Equal(["UiPath.System.Activities"], result.UnresolvedReferences);
        Assert.Single(result.Matches); // declared symbols still resolve in partial mode
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/UiPath.Engineering.Mcp.Core.Tests --filter "FullyQualifiedName~FindSymbolTests"`
Expected: FAIL — `CSharpAnalysisService` / DTOs do not exist (compile error).

- [ ] **Step 3: Implement DTOs, interface, and `FindSymbolAsync`**

Create `src/UiPath.Engineering.Mcp.Core/CodeAnalysis/CSharpAnalysisDtos.cs`:

```csharp
namespace UiPath.Engineering.Mcp.Core.CodeAnalysis;

/// <summary>
/// Base shape every C# analysis tool response carries: how much the results can be
/// trusted ("full" | "partial" | "syntaxOnly") and what could not be resolved.
/// </summary>
public abstract class CSharpAnalysisResult {
    public string AnalysisMode { get; set; } = "full";
    public List<string> UnresolvedReferences { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
    public bool HasCSharpFiles { get; set; } = true;
    public string? Note { get; set; }
}

public sealed class SymbolMatch {
    public string Name { get; init; } = string.Empty;
    public string Kind { get; init; } = string.Empty;
    public string? FilePath { get; init; }
    public int Line { get; init; }
    public string? ContainingType { get; init; }
    public string Signature { get; init; } = string.Empty;
}

public sealed class FindSymbolResult : CSharpAnalysisResult {
    public List<SymbolMatch> Matches { get; init; } = [];
}

public sealed class ReferenceMatch {
    public string FilePath { get; init; } = string.Empty;
    public int Line { get; init; }
    public string? ContainingMember { get; init; }
    public string Snippet { get; init; } = string.Empty;
}

public sealed class FindReferencesResult : CSharpAnalysisResult {
    public List<ReferenceMatch> References { get; init; } = [];
}

public sealed class CodeContextResult : CSharpAnalysisResult {
    public bool Found { get; set; }
    public string? Name { get; set; }
    public string? Kind { get; set; }
    public string? FilePath { get; set; }
    public int Line { get; set; }
    public string? ContainingType { get; set; }
    public string? Signature { get; set; }
    public List<string> CalledMethods { get; set; } = [];
    public List<string> ReferencedTypes { get; set; } = [];
    public string? Source { get; set; }
    public bool Truncated { get; set; }
}

public sealed class DiagnosticItem {
    public string FilePath { get; init; } = string.Empty;
    public int Line { get; init; }
    public int Column { get; init; }
    public string Code { get; init; } = string.Empty;
    public string Severity { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
}

public sealed class CompileDiagnosticsResult : CSharpAnalysisResult {
    public List<DiagnosticItem> Diagnostics { get; init; } = [];
    public int SuppressedMissingReferenceDiagnostics { get; set; }
}
```

Create `src/UiPath.Engineering.Mcp.Core/CodeAnalysis/ICSharpAnalysisService.cs`:

```csharp
namespace UiPath.Engineering.Mcp.Core.CodeAnalysis;

public interface ICSharpAnalysisService {
    Task<FindSymbolResult> FindSymbolAsync(string projectPath, string symbol, string? kind = null, CancellationToken cancellationToken = default);
    Task<FindReferencesResult> FindReferencesAsync(string projectPath, string symbol, CancellationToken cancellationToken = default);
    Task<CodeContextResult> GetCodeContextAsync(string projectPath, string? symbol = null, string? file = null, int? line = null, CancellationToken cancellationToken = default);
    Task<CompileDiagnosticsResult> GetDiagnosticsAsync(string projectPath, string? severity = null, CancellationToken cancellationToken = default);
}
```

Create `src/UiPath.Engineering.Mcp.Core/CodeAnalysis/CSharpAnalysisService.cs`:

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace UiPath.Engineering.Mcp.Core.CodeAnalysis;

/// <summary>
/// Semantic C# queries over the cached per-project <see cref="CSharpAnalysisContext"/>.
/// Stateless beyond the context cache; every method takes the project path.
/// </summary>
public sealed class CSharpAnalysisService : ICSharpAnalysisService {
    internal const int MaxResults = 200;
    internal const int MaxListItems = 25;
    internal const int MaxSourceLines = 200;

    private readonly ICSharpContextBuilder _contextBuilder;

    public CSharpAnalysisService(ICSharpContextBuilder contextBuilder) => _contextBuilder = contextBuilder;

    public async Task<FindSymbolResult> FindSymbolAsync(string projectPath, string symbol, string? kind = null, CancellationToken cancellationToken = default) {
        var context = await _contextBuilder.BuildAsync(projectPath, cancellationToken);

        var matches = context.Compilation
            .GetSymbolsWithName(symbol, SymbolFilter.All, cancellationToken)
            .Where(s => string.Equals(s.Name, symbol, StringComparison.Ordinal))
            .Where(s => s.Locations.Any(l => l.IsInSource))
            .Where(s => KindMatches(s, kind))
            .Take(MaxResults)
            .Select(ToSymbolMatch)
            .ToList();

        var result = new FindSymbolResult { Matches = matches };
        ApplyContext(result, context);
        return result;
    }

    public Task<FindReferencesResult> FindReferencesAsync(string projectPath, string symbol, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException(); // Task 6

    public Task<CodeContextResult> GetCodeContextAsync(string projectPath, string? symbol = null, string? file = null, int? line = null, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException(); // Task 7

    public Task<CompileDiagnosticsResult> GetDiagnosticsAsync(string projectPath, string? severity = null, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException(); // Task 8

    // --- shared helpers (also used by Tasks 6-8) -------------------------------

    internal static void ApplyContext(CSharpAnalysisResult result, CSharpAnalysisContext context) {
        result.AnalysisMode = context.Mode switch {
            CSharpAnalysisMode.Full => "full",
            CSharpAnalysisMode.Partial => "partial",
            _ => "syntaxOnly"
        };
        result.UnresolvedReferences = [.. context.UnresolvedReferences];
        result.Warnings = [.. context.Warnings];
        result.HasCSharpFiles = context.HasCSharpFiles;
        if (!context.HasCSharpFiles) {
            result.Note = "The project contains no C# files.";
        }
    }

    internal static bool KindMatches(ISymbol symbol, string? kind) => kind?.ToLowerInvariant() switch {
        null or "" => true,
        "method" => symbol.Kind == SymbolKind.Method,
        "property" => symbol.Kind == SymbolKind.Property,
        "field" => symbol.Kind == SymbolKind.Field,
        "class" => symbol is INamedTypeSymbol { TypeKind: TypeKind.Class },
        "interface" => symbol is INamedTypeSymbol { TypeKind: TypeKind.Interface },
        _ => true
    };

    internal static SymbolMatch ToSymbolMatch(ISymbol symbol) {
        var span = symbol.Locations.FirstOrDefault(l => l.IsInSource)?.GetLineSpan();
        return new SymbolMatch {
            Name = symbol.Name,
            Kind = symbol switch {
                INamedTypeSymbol type => type.TypeKind.ToString().ToLowerInvariant(),
                _ => symbol.Kind.ToString().ToLowerInvariant()
            },
            FilePath = span?.Path,
            Line = span is { } lineSpan ? lineSpan.StartLinePosition.Line + 1 : 0,
            ContainingType = symbol.ContainingType?.ToDisplayString(),
            Signature = symbol.ToDisplayString()
        };
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/UiPath.Engineering.Mcp.Core.Tests --filter "FullyQualifiedName~FindSymbolTests"`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add src/UiPath.Engineering.Mcp.Core/CodeAnalysis/CSharpAnalysisDtos.cs src/UiPath.Engineering.Mcp.Core/CodeAnalysis/ICSharpAnalysisService.cs src/UiPath.Engineering.Mcp.Core/CodeAnalysis/CSharpAnalysisService.cs tests/UiPath.Engineering.Mcp.Core.Tests/CSharpAnalysisServiceTestBase.cs tests/UiPath.Engineering.Mcp.Core.Tests/FindSymbolTests.cs
git commit -m "feat: add CSharpAnalysisService with find-symbol query"
```

---

### Task 6: `FindReferences`

**Files:**
- Modify: `src/UiPath.Engineering.Mcp.Core/CodeAnalysis/CSharpAnalysisService.cs` (replace the `FindReferencesAsync` stub)
- Test: `tests/UiPath.Engineering.Mcp.Core.Tests/FindReferencesTests.cs`

**Interfaces:**
- Consumes: `CSharpAnalysisResult`, `ReferenceMatch`, `FindReferencesResult`, `ApplyContext`, `MaxResults` (Task 5).
- Produces: `ICSharpAnalysisService.FindReferencesAsync` implementation consumed by `FindCodeReferencesTool` (Task 10).

- [ ] **Step 1: Write the failing tests**

Create `tests/UiPath.Engineering.Mcp.Core.Tests/FindReferencesTests.cs`:

```csharp
namespace UiPath.Engineering.Mcp.Core.Tests;

public class FindReferencesTests : CSharpAnalysisServiceTestBase {
    // Line map (1-based): 1 using System; | 2 blank | 3 namespace | 4 blank |
    // 5 class | 6 Execute | 7 Log("start"); | 8 return | 9 } | 10 blank |
    // 11 Log declaration | 12 Console | 13 } | 14 }
    private const string Source = """
        using System;

        namespace TestProcess;

        public class InvoiceFlow {
            public int Execute(string input, int count) {
                Log("start");
                return count + 1;
            }

            private void Log(string message) {
                Console.WriteLine(message);
            }
        }
        """;

    [Fact]
    public async Task FindReferences_MethodCall_ReturnsCallSiteWithMemberAndSnippet() {
        var service = CreateService(BuildContext(Source));

        var result = await service.FindReferencesAsync(Root, "Log");

        var reference = Assert.Single(result.References);
        Assert.Equal(FlowCs, reference.FilePath);
        Assert.Equal(7, reference.Line);
        Assert.Equal("Execute", reference.ContainingMember);
        Assert.Contains("Log(", reference.Snippet);
    }

    [Fact]
    public async Task FindReferences_UnknownName_FallsBackToIdentifierMatching() {
        // "ExternalCall" is not declared anywhere: semantic matching finds no target,
        // so the result relies on identifier-name matching and still locates the call.
        const string source = """
            public class Flow {
                public void Execute() {
                    ExternalCall();
                }
            }
            """;
        var service = CreateService(BuildContext(source));

        var result = await service.FindReferencesAsync(Root, "ExternalCall");

        var reference = Assert.Single(result.References);
        Assert.Equal(3, reference.Line);
        Assert.Equal("Execute", reference.ContainingMember);
    }

    [Fact]
    public async Task FindReferences_DeclarationOnly_ReturnsNoReferences() {
        // "InvoiceFlow" is declared but never used: constructors/inheritance absent,
        // so there are zero reference sites (the declaration itself is never a match).
        var service = CreateService(BuildContext(Source));

        var result = await service.FindReferencesAsync(Root, "WriteLine");

        Assert.Empty(result.References); // WriteLine's declaration lives in metadata, not source
    }
}
```

Note on the third test: `WriteLine` appears as a `SimpleNameSyntax` in `Console.WriteLine(...)`, but its declaration is in referenced metadata, not source. `GetSymbolsWithName` returns no source-declared target; with zero targets the name-matching fallback WOULD match it — so this test pins the intended fallback behavior: **fallback matches only when the name is also not resolvable semantically at that node**. Adjust the implementation rule accordingly: with zero target declarations, a node matches only if `GetSymbolInfo(node).Symbol` is null (truly unresolved identifier). `WriteLine` resolves to `System.Console.WriteLine` → excluded; `ExternalCall` resolves to nothing → included.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/UiPath.Engineering.Mcp.Core.Tests --filter "FullyQualifiedName~FindReferencesTests"`
Expected: FAIL — `FindReferencesAsync` throws `NotImplementedException`.

- [ ] **Step 3: Implement `FindReferencesAsync`**

In `src/UiPath.Engineering.Mcp.Core/CodeAnalysis/CSharpAnalysisService.cs`, replace the `FindReferencesAsync` stub with:

```csharp
public async Task<FindReferencesResult> FindReferencesAsync(string projectPath, string symbol, CancellationToken cancellationToken = default) {
    var context = await _contextBuilder.BuildAsync(projectPath, cancellationToken);

    // Source-declared target symbols with this exact name (may be empty: the symbol
    // can live in referenced metadata or be unresolvable in degraded modes).
    var targets = context.Compilation
        .GetSymbolsWithName(symbol, SymbolFilter.All, cancellationToken)
        .Where(s => string.Equals(s.Name, symbol, StringComparison.Ordinal))
        .Where(s => s.Locations.Any(l => l.IsInSource))
        .ToList();

    var matches = new List<ReferenceMatch>();
    foreach (var tree in context.Compilation.SyntaxTrees) {
        cancellationToken.ThrowIfCancellationRequested();
        var model = context.Compilation.GetSemanticModel(tree);
        var root = await tree.GetRootAsync(cancellationToken);
        var text = await tree.GetTextAsync(cancellationToken);

        foreach (var node in root.DescendantNodes().OfType<SimpleNameSyntax>()) {
            if (matches.Count >= MaxResults) {
                break;
            }
            if (!string.Equals(node.Identifier.Text, symbol, StringComparison.Ordinal)) {
                continue;
            }

            // Declaration identifiers are tokens, not SimpleNameSyntax nodes, so
            // declarations never appear here; every candidate is a usage site.
            var info = model.GetSymbolInfo(node, cancellationToken);
            var candidate = info.Symbol ?? info.CandidateSymbols.FirstOrDefault();

            var isReference = targets.Count > 0
                ? targets.Any(t => SymbolMatchesTarget(candidate, t))
                : candidate is null; // fallback: only truly unresolved identifiers
            if (!isReference) {
                continue;
            }

            matches.Add(ToReferenceMatch(text, node));
        }
    }

    var result = new FindReferencesResult { References = matches };
    ApplyContext(result, context);
    if (targets.Count == 0) {
        result.Note = $"'{symbol}' is not declared in this project's source; matches are identifier-based and may include false positives.";
    }
    return result;
}
```

Add these private helpers to `CSharpAnalysisService`:

```csharp
private static bool SymbolMatchesTarget(ISymbol? candidate, ISymbol target) {
    if (candidate is null) {
        return false;
    }
    return SymbolEqualityComparer.Default.Equals(candidate, target)
        || SymbolEqualityComparer.Default.Equals(candidate.OriginalDefinition, target.OriginalDefinition);
}

private static ReferenceMatch ToReferenceMatch(SourceText text, SimpleNameSyntax node) {
    var span = node.GetLocation().GetLineSpan();
    var containing = node.Ancestors().OfType<MemberDeclarationSyntax>().FirstOrDefault();
    var containingName = containing switch {
        BaseMethodDeclarationSyntax method => method.Identifier.Text,
        BaseTypeDeclarationSyntax type => type.Identifier.Text,
        BasePropertyDeclarationSyntax property => property.Identifier.Text,
        _ => null
    };
    var lineIndex = span.StartLinePosition.Line;
    return new ReferenceMatch {
        FilePath = span.Path ?? string.Empty,
        Line = lineIndex + 1,
        ContainingMember = containingName,
        Snippet = lineIndex < text.Lines.Count ? text.Lines[lineIndex].ToString().Trim() : string.Empty
    };
}
```

Add `using Microsoft.CodeAnalysis.Text;` to the file's usings (for `SourceText`).

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/UiPath.Engineering.Mcp.Core.Tests --filter "FullyQualifiedName~FindReferencesTests|FullyQualifiedName~FindSymbolTests"`
Expected: PASS (FindSymbol still green + 3 new tests).

- [ ] **Step 5: Commit**

```bash
git add src/UiPath.Engineering.Mcp.Core/CodeAnalysis/CSharpAnalysisService.cs tests/UiPath.Engineering.Mcp.Core.Tests/FindReferencesTests.cs
git commit -m "feat: add find-references query with semantic + identifier fallback"
```

---

### Task 7: `GetCodeContext`

**Files:**
- Modify: `src/UiPath.Engineering.Mcp.Core/CodeAnalysis/CSharpAnalysisService.cs` (replace the `GetCodeContextAsync` stub)
- Test: `tests/UiPath.Engineering.Mcp.Core.Tests/GetCodeContextTests.cs`

**Interfaces:**
- Consumes: `CodeContextResult`, `ApplyContext`, `MaxListItems`, `MaxSourceLines` (Task 5).
- Produces: `ICSharpAnalysisService.GetCodeContextAsync` implementation consumed by `GetCodeContextTool` (Task 9).

- [ ] **Step 1: Write the failing tests**

Create `tests/UiPath.Engineering.Mcp.Core.Tests/GetCodeContextTests.cs`:

```csharp
namespace UiPath.Engineering.Mcp.Core.Tests;

public class GetCodeContextTests : CSharpAnalysisServiceTestBase {
    // Line map (1-based): 1 namespace | 2 blank | 3 class InvoiceFlow | 4 Execute |
    // 5 var helper | 6 helper.Prepare | 7 return | 8 } | 9 } | 10 class Invoice |
    // 11 Total | 12 } | 13 class InvoiceHelper | 14 Prepare | 15 } | 16 }
    private const string Source = """
        namespace TestProcess;

        public class InvoiceFlow {
            public int Execute(Invoice invoice) {
                var helper = new InvoiceHelper();
                helper.Prepare(invoice);
                return invoice.Total;
            }
        }

        public class Invoice {
            public int Total { get; set; }
        }

        public class InvoiceHelper {
            public void Prepare(Invoice invoice) { }
        }
        """;

    [Fact]
    public async Task GetCodeContext_BySymbol_ReturnsMemberContext() {
        var service = CreateService(BuildContext(Source));

        var result = await service.GetCodeContextAsync(Root, symbol: "Execute");

        Assert.True(result.Found);
        Assert.Equal("Execute", result.Name);
        Assert.Equal("method", result.Kind);
        Assert.Equal(FlowCs, result.FilePath);
        Assert.Equal(4, result.Line);
        Assert.Equal("TestProcess.InvoiceFlow", result.ContainingType);
        Assert.Contains("Invoice", result.Signature);
        Assert.Contains("InvoiceHelper.Prepare", result.CalledMethods);
        Assert.Contains("Invoice", result.ReferencedTypes);
        Assert.Contains("InvoiceHelper", result.ReferencedTypes);
        Assert.Contains("helper.Prepare(invoice);", result.Source);
        Assert.False(result.Truncated);
    }

    [Fact]
    public async Task GetCodeContext_ByFileAndLine_ReturnsEnclosingMember() {
        var service = CreateService(BuildContext(Source));

        var result = await service.GetCodeContextAsync(Root, file: FlowCs, line: 6);

        Assert.True(result.Found);
        Assert.Equal("Execute", result.Name);
    }

    [Fact]
    public async Task GetCodeContext_UnknownSymbol_ReturnsFoundFalseWithNote() {
        var service = CreateService(BuildContext(Source));

        var result = await service.GetCodeContextAsync(Root, symbol: "Missing");

        Assert.False(result.Found);
        Assert.NotNull(result.Note);
    }

    [Fact]
    public async Task GetCodeContext_NoArguments_ReturnsFoundFalseWithNote() {
        var service = CreateService(BuildContext(Source));

        var result = await service.GetCodeContextAsync(Root);

        Assert.False(result.Found);
        Assert.NotNull(result.Note);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/UiPath.Engineering.Mcp.Core.Tests --filter "FullyQualifiedName~GetCodeContextTests"`
Expected: FAIL — `GetCodeContextAsync` throws `NotImplementedException`.

- [ ] **Step 3: Implement `GetCodeContextAsync`**

In `src/UiPath.Engineering.Mcp.Core/CodeAnalysis/CSharpAnalysisService.cs`, replace the `GetCodeContextAsync` stub with:

```csharp
public async Task<CodeContextResult> GetCodeContextAsync(string projectPath, string? symbol = null, string? file = null, int? line = null, CancellationToken cancellationToken = default) {
    var context = await _contextBuilder.BuildAsync(projectPath, cancellationToken);
    var result = new CodeContextResult();
    ApplyContext(result, context);

    var located = await LocateMemberAsync(context, symbol, file, line, cancellationToken);
    if (located is null) {
        result.Found = false;
        result.Note = symbol is null && file is null
            ? "Provide either 'symbol' or 'file' + 'line'."
            : "No matching member found for the given symbol or location.";
        return result;
    }

    var (member, model) = located.Value;
    var declared = model.GetDeclaredSymbol(member, cancellationToken);
    var span = member.GetLocation().GetLineSpan();

    result.Found = true;
    result.Name = member switch {
        BaseMethodDeclarationSyntax method => method.Identifier.Text,
        BaseTypeDeclarationSyntax type => type.Identifier.Text,
        BasePropertyDeclarationSyntax property => property.Identifier.Text,
        _ => member.GetType().Name
    };
    result.Kind = declared is not null
        ? ToSymbolMatch(declared).Kind
        : member.Kind().ToString().Replace("Declaration", string.Empty).ToLowerInvariant();
    result.FilePath = span.Path;
    result.Line = span.StartLinePosition.Line + 1;
    result.ContainingType = declared?.ContainingType?.ToDisplayString();
    result.Signature = declared?.ToDisplayString();

    result.CalledMethods = member.DescendantNodes().OfType<InvocationExpressionSyntax>()
        .Select(invocation => model.GetSymbolInfo(invocation, cancellationToken).Symbol as IMethodSymbol)
        .Where(method => method is not null)
        .Select(method => $"{method!.ContainingType?.Name}.{method.Name}")
        .Distinct()
        .Take(MaxListItems)
        .ToList();

    result.ReferencedTypes = member.DescendantNodes().OfType<TypeSyntax>()
        .Select(typeNode => model.GetTypeInfo(typeNode, cancellationToken).Type)
        .Where(type => type is { SpecialType: SpecialType.None } and not IErrorTypeSymbol)
        .Select(type => type!.Name)
        .Where(name => !string.IsNullOrEmpty(name))
        .Distinct()
        .Take(MaxListItems)
        .ToList();

    var source = member.ToString();
    var sourceLines = source.Split('\n');
    result.Truncated = sourceLines.Length > MaxSourceLines;
    result.Source = result.Truncated
        ? string.Join('\n', sourceLines.Take(MaxSourceLines))
        : source;
    return result;
}
```

Add these private helpers to `CSharpAnalysisService`:

```csharp
private sealed record LocatedMember(MemberDeclarationSyntax Member, SemanticModel Model);

private static async Task<LocatedMember?> LocateMemberAsync(
    CSharpAnalysisContext context, string? symbol, string? file, int? line, CancellationToken cancellationToken) {
    if (!string.IsNullOrWhiteSpace(symbol)) {
        var target = context.Compilation
            .GetSymbolsWithName(symbol, SymbolFilter.All, cancellationToken)
            .Where(s => string.Equals(s.Name, symbol, StringComparison.Ordinal))
            .Where(s => s.Locations.Any(l => l.IsInSource))
            .OrderByDescending(s => s.Kind == SymbolKind.Method) // prefer methods over types
            .FirstOrDefault();
        var reference = target?.DeclaringSyntaxReferences.FirstOrDefault();
        if (reference is null) {
            return null;
        }
        var node = reference.GetSyntax(cancellationToken);
        var member = node.AncestorsAndSelf().OfType<MemberDeclarationSyntax>().FirstOrDefault();
        return member is null
            ? null
            : new LocatedMember(member, context.Compilation.GetSemanticModel(member.SyntaxTree));
    }

    if (!string.IsNullOrWhiteSpace(file) && line is > 0) {
        var tree = context.Compilation.SyntaxTrees.FirstOrDefault(t =>
            string.Equals(t.FilePath, file, StringComparison.OrdinalIgnoreCase));
        if (tree is null) {
            return null;
        }
        var text = await tree.GetTextAsync(cancellationToken);
        if (line.Value > text.Lines.Count) {
            return null;
        }
        var position = text.Lines[line.Value - 1].Start;
        var root = await tree.GetRootAsync(cancellationToken);
        var token = root.FindToken(position);
        var member = token.Parent?.AncestorsAndSelf().OfType<MemberDeclarationSyntax>().FirstOrDefault();
        return member is null
            ? null
            : new LocatedMember(member, context.Compilation.GetSemanticModel(tree));
    }

    return null;
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/UiPath.Engineering.Mcp.Core.Tests --filter "FullyQualifiedName~GetCodeContextTests"`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add src/UiPath.Engineering.Mcp.Core/CodeAnalysis/CSharpAnalysisService.cs tests/UiPath.Engineering.Mcp.Core.Tests/GetCodeContextTests.cs
git commit -m "feat: add get-code-context query (symbol or file+line)"
```

---

### Task 8: `GetDiagnostics`

**Files:**
- Modify: `src/UiPath.Engineering.Mcp.Core/CodeAnalysis/CSharpAnalysisService.cs` (replace the `GetDiagnosticsAsync` stub)
- Test: `tests/UiPath.Engineering.Mcp.Core.Tests/GetDiagnosticsTests.cs`

**Interfaces:**
- Consumes: `CompileDiagnosticsResult`, `DiagnosticItem`, `ApplyContext` (Task 5).
- Produces: `ICSharpAnalysisService.GetDiagnosticsAsync` implementation consumed by `GetCompileErrorsTool` (Task 10).

- [ ] **Step 1: Write the failing tests**

Create `tests/UiPath.Engineering.Mcp.Core.Tests/GetDiagnosticsTests.cs`:

```csharp
using UiPath.Engineering.Mcp.Core.CodeAnalysis;

namespace UiPath.Engineering.Mcp.Core.Tests;

public class GetDiagnosticsTests : CSharpAnalysisServiceTestBase {
    // Line map (1-based): 1 class Broken | 2 Execute | 3 return missingName | 4 } | 5 }
    private const string BrokenSource = """
        public class Broken {
            public int Execute() {
                return missingName + 1;
            }
        }
        """;

    [Fact]
    public async Task GetDiagnostics_UndefinedIdentifier_ReturnsCs0103() {
        var service = CreateService(BuildContext(BrokenSource));

        var result = await service.GetDiagnosticsAsync(Root);

        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("CS0103", diagnostic.Code);
        Assert.Equal("error", diagnostic.Severity);
        Assert.Equal(3, diagnostic.Line);
        Assert.True(diagnostic.Column > 0);
        Assert.Equal(FlowCs, diagnostic.FilePath);
        Assert.Contains("missingName", diagnostic.Message);
    }

    [Fact]
    public async Task GetDiagnostics_CleanSource_ReturnsEmpty() {
        const string source = """
            public class Clean {
                public int Execute() {
                    return 1 + 1;
                }
            }
            """;
        var service = CreateService(BuildContext(source));

        var result = await service.GetDiagnosticsAsync(Root);

        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public async Task GetDiagnostics_PartialMode_SuppressesMissingReferenceNoise() {
        // No runtime references at all: 'Missing.Thing' yields CS0246 (among other noise).
        const string source = """
            public class Uses {
                public Missing.Thing Make() => new Missing.Thing();
            }
            """;
        var context = BuildContext(source, mode: CSharpAnalysisMode.Partial,
            withRuntimeReferences: false, unresolved: ["Missing.Package"]);
        var service = CreateService(context);

        var result = await service.GetDiagnosticsAsync(Root);

        Assert.DoesNotContain(result.Diagnostics, d => d.Code == "CS0246");
        Assert.True(result.SuppressedMissingReferenceDiagnostics >= 1);
        Assert.NotNull(result.Note);
        Assert.Equal("partial", result.AnalysisMode);
    }

    [Fact]
    public async Task GetDiagnostics_SyntaxOnlyMode_ReturnsEmptyWithNote() {
        var context = BuildContext(BrokenSource, mode: CSharpAnalysisMode.SyntaxOnly);
        var service = CreateService(context);

        var result = await service.GetDiagnosticsAsync(Root);

        Assert.Empty(result.Diagnostics);
        Assert.NotNull(result.Note);
        Assert.Equal("syntaxOnly", result.AnalysisMode);
    }
}
```

Note on the clean-source test: runtime reference folders also ship some assemblies that produce harmless info-level diagnostics; the default severity filter (`error` and above) plus `Location.IsInSource` keeps the result empty. If you see metadata warnings locally, verify the filter is applied — do not weaken the test.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/UiPath.Engineering.Mcp.Core.Tests --filter "FullyQualifiedName~GetDiagnosticsTests"`
Expected: FAIL — `GetDiagnosticsAsync` throws `NotImplementedException`.

- [ ] **Step 3: Implement `GetDiagnosticsAsync`**

In `src/UiPath.Engineering.Mcp.Core/CodeAnalysis/CSharpAnalysisService.cs`, replace the `GetDiagnosticsAsync` stub with:

```csharp
private static readonly HashSet<string> MissingReferenceCodes = new(StringComparer.Ordinal) {
    "CS0234", // type or namespace does not exist in namespace
    "CS0246", // type or namespace could not be found
    "CS0012"  // type is defined in an assembly that is not referenced
};

public async Task<CompileDiagnosticsResult> GetDiagnosticsAsync(string projectPath, string? severity = null, CancellationToken cancellationToken = default) {
    var context = await _contextBuilder.BuildAsync(projectPath, cancellationToken);
    var result = new CompileDiagnosticsResult();
    ApplyContext(result, context);

    if (context.Mode == CSharpAnalysisMode.SyntaxOnly) {
        result.Note = "References could not be resolved; compiler diagnostics are unavailable in syntaxOnly mode.";
        return result;
    }

    var minSeverity = severity?.ToLowerInvariant() switch {
        "all" => DiagnosticSeverity.Hidden,
        "warning" => DiagnosticSeverity.Warning,
        _ => DiagnosticSeverity.Error
    };

    foreach (var diagnostic in context.Compilation.GetDiagnostics(cancellationToken)) {
        if (diagnostic.Severity < minSeverity || !diagnostic.Location.IsInSource) {
            continue;
        }
        if (context.Mode == CSharpAnalysisMode.Partial && MissingReferenceCodes.Contains(diagnostic.Id)) {
            result.SuppressedMissingReferenceDiagnostics++;
            continue;
        }

        var span = diagnostic.Location.GetLineSpan();
        result.Diagnostics.Add(new DiagnosticItem {
            FilePath = span.Path ?? string.Empty,
            Line = span.StartLinePosition.Line + 1,
            Column = span.StartLinePosition.Character + 1,
            Code = diagnostic.Id,
            Severity = diagnostic.Severity.ToString().ToLowerInvariant(),
            Message = diagnostic.GetMessage()
        });
    }

    if (result.SuppressedMissingReferenceDiagnostics > 0) {
        result.Note = $"Suppressed {result.SuppressedMissingReferenceDiagnostics} diagnostics caused by unresolved references ({string.Join(", ", context.UnresolvedReferences)}). Resolve the packages and re-run for full diagnostics.";
    }
    return result;
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/UiPath.Engineering.Mcp.Core.Tests --filter "FullyQualifiedName~GetDiagnosticsTests"`
Expected: PASS (4 tests).

- [ ] **Step 5: Run the whole Core suite**

Run: `dotnet test tests/UiPath.Engineering.Mcp.Core.Tests`
Expected: PASS (all Core tests, including everything from Tasks 1-8).

- [ ] **Step 6: Commit**

```bash
git add src/UiPath.Engineering.Mcp.Core/CodeAnalysis/CSharpAnalysisService.cs tests/UiPath.Engineering.Mcp.Core.Tests/GetDiagnosticsTests.cs
git commit -m "feat: add compiler diagnostics query with partial-mode noise suppression"
```

---

### Task 9: `FindCodeSymbolTool` + `GetCodeContextTool`

**Files:**
- Create: `src/UiPath.Engineering.Mcp.Tools/FindCodeSymbolTool.cs`
- Create: `src/UiPath.Engineering.Mcp.Tools/GetCodeContextTool.cs`
- Modify: `tests/UiPath.Engineering.Mcp.Tools.Tests/Fakes.cs` (add `FakeCSharpAnalysisService`)
- Test: `tests/UiPath.Engineering.Mcp.Tools.Tests/CodeAnalysisToolsTests.cs`

**Interfaces:**
- Consumes: `ICSharpAnalysisService` (Task 5-8), `ToolResults.GuardProject`, `ToolResults.Ok`, `ToolResults.FromException`.
- Produces: MCP tools `find_code_symbol` and `get_code_context` (registered via assembly scan — no registration code needed).

- [ ] **Step 1: Add the fake to `Fakes.cs`**

Append to `tests/UiPath.Engineering.Mcp.Tools.Tests/Fakes.cs` (add `using UiPath.Engineering.Mcp.Core.CodeAnalysis;` at the top):

```csharp
internal sealed class FakeCSharpAnalysisService : ICSharpAnalysisService {
    public FindSymbolResult SymbolResult { get; set; } = new();
    public FindReferencesResult ReferencesResult { get; set; } = new();
    public CodeContextResult ContextResult { get; set; } = new() { Found = true };
    public CompileDiagnosticsResult DiagnosticsResult { get; set; } = new();
    public Exception? ToThrow { get; set; }
    public string? LastProjectPath { get; private set; }
    public string? LastSymbol { get; private set; }
    public string? LastKind { get; private set; }
    public string? LastFile { get; private set; }
    public int? LastLine { get; private set; }
    public string? LastSeverity { get; private set; }

    public Task<FindSymbolResult> FindSymbolAsync(string projectPath, string symbol, string? kind = null, CancellationToken cancellationToken = default) {
        if (ToThrow is not null) throw ToThrow;
        LastProjectPath = projectPath; LastSymbol = symbol; LastKind = kind;
        return Task.FromResult(SymbolResult);
    }

    public Task<FindReferencesResult> FindReferencesAsync(string projectPath, string symbol, CancellationToken cancellationToken = default) {
        if (ToThrow is not null) throw ToThrow;
        LastProjectPath = projectPath; LastSymbol = symbol;
        return Task.FromResult(ReferencesResult);
    }

    public Task<CodeContextResult> GetCodeContextAsync(string projectPath, string? symbol = null, string? file = null, int? line = null, CancellationToken cancellationToken = default) {
        if (ToThrow is not null) throw ToThrow;
        LastProjectPath = projectPath; LastSymbol = symbol; LastFile = file; LastLine = line;
        return Task.FromResult(ContextResult);
    }

    public Task<CompileDiagnosticsResult> GetDiagnosticsAsync(string projectPath, string? severity = null, CancellationToken cancellationToken = default) {
        if (ToThrow is not null) throw ToThrow;
        LastProjectPath = projectPath; LastSeverity = severity;
        return Task.FromResult(DiagnosticsResult);
    }
}
```

- [ ] **Step 2: Write the failing tests**

Create `tests/UiPath.Engineering.Mcp.Tools.Tests/CodeAnalysisToolsTests.cs`:

```csharp
using UiPath.Engineering.Mcp.Core.CodeAnalysis;

namespace UiPath.Engineering.Mcp.Tools.Tests;

public class CodeAnalysisToolsTests {
    private static FakeFilesystemProvider ProjectFilesystem() =>
        new() { Allowed = true, ProjectJson = "/projects/testProcess/project.json" };

    // --- find_code_symbol ---

    [Fact]
    public async Task FindCodeSymbol_PathNotAllowed_ReturnsError() {
        var tool = new FindCodeSymbolTool(new FakeFilesystemProvider { Allowed = false }, new FakeCSharpAnalysisService());

        var result = await tool.FindCodeSymbol("/not/allowed", "Execute");

        Assert.Equal("error", result.Status);
        Assert.Equal("Path not allowed.", result.Summary);
    }

    [Fact]
    public async Task FindCodeSymbol_HappyPath_ReturnsMatchesAndForwardsArguments() {
        var analysis = new FakeCSharpAnalysisService {
            SymbolResult = new FindSymbolResult {
                Matches = [new SymbolMatch { Name = "Execute", Kind = "method", FilePath = "Flow.cs", Line = 6 }]
            }
        };
        var tool = new FindCodeSymbolTool(ProjectFilesystem(), analysis);

        var result = await tool.FindCodeSymbol("/projects/testProcess", "Execute", kind: "method");

        Assert.Equal("success", result.Status);
        Assert.Equal("/projects/testProcess", analysis.LastProjectPath);
        Assert.Equal("Execute", analysis.LastSymbol);
        Assert.Equal("method", analysis.LastKind);
        var data = Assert.IsType<FindSymbolResult>(result.Data);
        Assert.Single(data.Matches);
    }

    [Fact]
    public async Task FindCodeSymbol_ServiceThrows_ReturnsStructuredError() {
        var analysis = new FakeCSharpAnalysisService { ToThrow = new InvalidOperationException("boom") };
        var tool = new FindCodeSymbolTool(ProjectFilesystem(), analysis);

        var result = await tool.FindCodeSymbol("/projects/testProcess", "Execute");

        Assert.Equal("error", result.Status);
        Assert.Contains("boom", result.Errors);
    }

    // --- get_code_context ---

    [Fact]
    public async Task GetCodeContext_PathNotAllowed_ReturnsError() {
        var tool = new GetCodeContextTool(new FakeFilesystemProvider { Allowed = false }, new FakeCSharpAnalysisService());

        var result = await tool.GetCodeContext("/not/allowed", symbol: "Execute");

        Assert.Equal("error", result.Status);
        Assert.Equal("Path not allowed.", result.Summary);
    }

    [Fact]
    public async Task GetCodeContext_BySymbol_ForwardsArguments() {
        var analysis = new FakeCSharpAnalysisService {
            ContextResult = new CodeContextResult { Found = true, Name = "Execute", Signature = "Execute()" }
        };
        var tool = new GetCodeContextTool(ProjectFilesystem(), analysis);

        var result = await tool.GetCodeContext("/projects/testProcess", symbol: "Execute");

        Assert.Equal("success", result.Status);
        Assert.Equal("Execute", analysis.LastSymbol);
        var data = Assert.IsType<CodeContextResult>(result.Data);
        Assert.True(data.Found);
    }

    [Fact]
    public async Task GetCodeContext_ByFileAndLine_ForwardsArguments() {
        var analysis = new FakeCSharpAnalysisService();
        var tool = new GetCodeContextTool(ProjectFilesystem(), analysis);

        await tool.GetCodeContext("/projects/testProcess", file: "Flow.cs", line: 6);

        Assert.Equal("Flow.cs", analysis.LastFile);
        Assert.Equal(6, analysis.LastLine);
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test tests/UiPath.Engineering.Mcp.Tools.Tests --filter "FullyQualifiedName~CodeAnalysisToolsTests"`
Expected: FAIL — tool types do not exist (compile error).

- [ ] **Step 4: Implement the two tools**

Create `src/UiPath.Engineering.Mcp.Tools/FindCodeSymbolTool.cs`:

```csharp
using System.ComponentModel;
using System.Diagnostics;
using ModelContextProtocol.Server;
using UiPath.Engineering.Mcp.Core.Abstractions;
using UiPath.Engineering.Mcp.Core.CodeAnalysis;
using UiPath.Engineering.Mcp.Core.Models;

namespace UiPath.Engineering.Mcp.Tools;

[McpServerToolType]
public sealed class FindCodeSymbolTool {
    private readonly IFilesystemProvider _filesystem;
    private readonly ICSharpAnalysisService _analysis;

    public FindCodeSymbolTool(IFilesystemProvider filesystem, ICSharpAnalysisService analysis) {
        _filesystem = filesystem;
        _analysis = analysis;
    }

    [McpServerTool, Description("Finds C# symbols (methods, classes, properties, fields, interfaces) by exact name in a UiPath project using Roslyn semantic analysis. Prefer this over reading whole .cs files when you need to locate a definition.")]
    public async Task<ToolResult> FindCodeSymbol(
        [Description("Absolute path to the UiPath project directory.")] string projectPath,
        [Description("Exact symbol name to find, e.g. 'ProcessTransaction'.")] string symbol,
        [Description("Optional kind filter: method, property, field, class, interface.")] string? kind = null,
        CancellationToken cancellationToken = default) {
        var sw = Stopwatch.StartNew();

        if (ToolResults.GuardProject(_filesystem, projectPath, sw) is { } guardFailure) {
            return guardFailure;
        }

        try {
            var result = await _analysis.FindSymbolAsync(projectPath, symbol, kind, cancellationToken);
            var summary = result.Matches.Count == 0
                ? $"No symbols named '{symbol}' found."
                : $"Found {result.Matches.Count} symbol(s) named '{symbol}'.";
            return ToolResults.Ok(summary, result, sw, result.Warnings);
        } catch (Exception ex) {
            return ToolResults.FromException(ex, "Symbol search failed.", sw);
        }
    }
}
```

Create `src/UiPath.Engineering.Mcp.Tools/GetCodeContextTool.cs`:

```csharp
using System.ComponentModel;
using System.Diagnostics;
using ModelContextProtocol.Server;
using UiPath.Engineering.Mcp.Core.Abstractions;
using UiPath.Engineering.Mcp.Core.CodeAnalysis;
using UiPath.Engineering.Mcp.Core.Models;

namespace UiPath.Engineering.Mcp.Tools;

[McpServerToolType]
public sealed class GetCodeContextTool {
    private readonly IFilesystemProvider _filesystem;
    private readonly ICSharpAnalysisService _analysis;

    public GetCodeContextTool(IFilesystemProvider filesystem, ICSharpAnalysisService analysis) {
        _filesystem = filesystem;
        _analysis = analysis;
    }

    [McpServerTool, Description("Returns the semantic context of one C# member (a method, class, or property) in a UiPath project: signature, containing type, called methods, referenced types, and the member's source. Locate the member by 'symbol' name or by 'file' + 'line'. Prefer this over reading whole .cs files.")]
    public async Task<ToolResult> GetCodeContext(
        [Description("Absolute path to the UiPath project directory.")] string projectPath,
        [Description("Symbol name to inspect, e.g. 'ProcessTransaction'.")] string? symbol = null,
        [Description("Path of the .cs file (used with 'line').")] string? file = null,
        [Description("1-based line number inside 'file'; the enclosing member is returned.")] int? line = null,
        CancellationToken cancellationToken = default) {
        var sw = Stopwatch.StartNew();

        if (ToolResults.GuardProject(_filesystem, projectPath, sw) is { } guardFailure) {
            return guardFailure;
        }

        try {
            var result = await _analysis.GetCodeContextAsync(projectPath, symbol, file, line, cancellationToken);
            var summary = result.Found
                ? $"Context for '{result.Name}'."
                : "No matching member found.";
            return ToolResults.Ok(summary, result, sw, result.Warnings);
        } catch (Exception ex) {
            return ToolResults.FromException(ex, "Failed to get code context.", sw);
        }
    }
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/UiPath.Engineering.Mcp.Tools.Tests --filter "FullyQualifiedName~CodeAnalysisToolsTests"`
Expected: PASS (6 tests).

- [ ] **Step 6: Commit**

```bash
git add src/UiPath.Engineering.Mcp.Tools/FindCodeSymbolTool.cs src/UiPath.Engineering.Mcp.Tools/GetCodeContextTool.cs tests/UiPath.Engineering.Mcp.Tools.Tests/Fakes.cs tests/UiPath.Engineering.Mcp.Tools.Tests/CodeAnalysisToolsTests.cs
git commit -m "feat: add find_code_symbol and get_code_context MCP tools"
```

---

### Task 10: `FindCodeReferencesTool` + `GetCompileErrorsTool`

**Files:**
- Create: `src/UiPath.Engineering.Mcp.Tools/FindCodeReferencesTool.cs`
- Create: `src/UiPath.Engineering.Mcp.Tools/GetCompileErrorsTool.cs`
- Test: `tests/UiPath.Engineering.Mcp.Tools.Tests/CodeAnalysisToolsTests.cs` (append)

**Interfaces:**
- Consumes: `ICSharpAnalysisService.FindReferencesAsync` / `GetDiagnosticsAsync`, `FakeCSharpAnalysisService` (Task 9).
- Produces: MCP tools `find_code_references` and `get_compile_errors`.

- [ ] **Step 1: Write the failing tests**

Append to `tests/UiPath.Engineering.Mcp.Tools.Tests/CodeAnalysisToolsTests.cs` (inside the class):

```csharp
// --- find_code_references ---

[Fact]
public async Task FindCodeReferences_PathNotAllowed_ReturnsError() {
    var tool = new FindCodeReferencesTool(new FakeFilesystemProvider { Allowed = false }, new FakeCSharpAnalysisService());

    var result = await tool.FindCodeReferences("/not/allowed", "Log");

    Assert.Equal("error", result.Status);
    Assert.Equal("Path not allowed.", result.Summary);
}

[Fact]
public async Task FindCodeReferences_HappyPath_ReturnsReferences() {
    var analysis = new FakeCSharpAnalysisService {
        ReferencesResult = new FindReferencesResult {
            References = [new ReferenceMatch { FilePath = "Flow.cs", Line = 7, ContainingMember = "Execute", Snippet = "Log(\"start\");" }]
        }
    };
    var tool = new FindCodeReferencesTool(ProjectFilesystem(), analysis);

    var result = await tool.FindCodeReferences("/projects/testProcess", "Log");

    Assert.Equal("success", result.Status);
    Assert.Equal("Log", analysis.LastSymbol);
    var data = Assert.IsType<FindReferencesResult>(result.Data);
    Assert.Single(data.References);
}

// --- get_compile_errors ---

[Fact]
public async Task GetCompileErrors_PathNotAllowed_ReturnsError() {
    var tool = new GetCompileErrorsTool(new FakeFilesystemProvider { Allowed = false }, new FakeCSharpAnalysisService());

    var result = await tool.GetCompileErrors("/not/allowed");

    Assert.Equal("error", result.Status);
    Assert.Equal("Path not allowed.", result.Summary);
}

[Fact]
public async Task GetCompileErrors_HappyPath_ReturnsDiagnosticsAndForwardsSeverity() {
    var analysis = new FakeCSharpAnalysisService {
        DiagnosticsResult = new CompileDiagnosticsResult {
            Diagnostics = [new DiagnosticItem { FilePath = "Flow.cs", Line = 3, Column = 16, Code = "CS0103", Severity = "error", Message = "The name 'missingName' does not exist in the current context" }]
        }
    };
    var tool = new GetCompileErrorsTool(ProjectFilesystem(), analysis);

    var result = await tool.GetCompileErrors("/projects/testProcess", severity: "warning");

    Assert.Equal("success", result.Status);
    Assert.Equal("warning", analysis.LastSeverity);
    var data = Assert.IsType<CompileDiagnosticsResult>(result.Data);
    var diagnostic = Assert.Single(data.Diagnostics);
    Assert.Equal("CS0103", diagnostic.Code);
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/UiPath.Engineering.Mcp.Tools.Tests --filter "FullyQualifiedName~CodeAnalysisToolsTests"`
Expected: FAIL — tool types do not exist (compile error).

- [ ] **Step 3: Implement the two tools**

Create `src/UiPath.Engineering.Mcp.Tools/FindCodeReferencesTool.cs`:

```csharp
using System.ComponentModel;
using System.Diagnostics;
using ModelContextProtocol.Server;
using UiPath.Engineering.Mcp.Core.Abstractions;
using UiPath.Engineering.Mcp.Core.CodeAnalysis;
using UiPath.Engineering.Mcp.Core.Models;

namespace UiPath.Engineering.Mcp.Tools;

[McpServerToolType]
public sealed class FindCodeReferencesTool {
    private readonly IFilesystemProvider _filesystem;
    private readonly ICSharpAnalysisService _analysis;

    public FindCodeReferencesTool(IFilesystemProvider filesystem, ICSharpAnalysisService analysis) {
        _filesystem = filesystem;
        _analysis = analysis;
    }

    [McpServerTool, Description("Finds all usage sites of a C# symbol (method, class, property, field) across a UiPath project's .cs files using Roslyn semantic analysis. When the symbol is not declared in project source, falls back to identifier matching and says so in the result.")]
    public async Task<ToolResult> FindCodeReferences(
        [Description("Absolute path to the UiPath project directory.")] string projectPath,
        [Description("Exact symbol name whose references to find, e.g. 'ProcessTransaction'.")] string symbol,
        CancellationToken cancellationToken = default) {
        var sw = Stopwatch.StartNew();

        if (ToolResults.GuardProject(_filesystem, projectPath, sw) is { } guardFailure) {
            return guardFailure;
        }

        try {
            var result = await _analysis.FindReferencesAsync(projectPath, symbol, cancellationToken);
            var summary = result.References.Count == 0
                ? $"No references to '{symbol}' found."
                : $"Found {result.References.Count} reference(s) to '{symbol}'.";
            return ToolResults.Ok(summary, result, sw, result.Warnings);
        } catch (Exception ex) {
            return ToolResults.FromException(ex, "Reference search failed.", sw);
        }
    }
}
```

Create `src/UiPath.Engineering.Mcp.Tools/GetCompileErrorsTool.cs`:

```csharp
using System.ComponentModel;
using System.Diagnostics;
using ModelContextProtocol.Server;
using UiPath.Engineering.Mcp.Core.Abstractions;
using UiPath.Engineering.Mcp.Core.CodeAnalysis;
using UiPath.Engineering.Mcp.Core.Models;

namespace UiPath.Engineering.Mcp.Tools;

[McpServerToolType]
public sealed class GetCompileErrorsTool {
    private readonly IFilesystemProvider _filesystem;
    private readonly ICSharpAnalysisService _analysis;

    public GetCompileErrorsTool(IFilesystemProvider filesystem, ICSharpAnalysisService analysis) {
        _filesystem = filesystem;
        _analysis = analysis;
    }

    [McpServerTool, Description("Returns structured C# compiler diagnostics (Roslyn) for a UiPath project without running a build: file, line, column, code, severity, message. Fast and in-memory. Use compile_project for the authoritative UiPath CLI build result.")]
    public async Task<ToolResult> GetCompileErrors(
        [Description("Absolute path to the UiPath project directory.")] string projectPath,
        [Description("Minimum severity to include: 'error' (default), 'warning', or 'all'.")] string? severity = null,
        CancellationToken cancellationToken = default) {
        var sw = Stopwatch.StartNew();

        if (ToolResults.GuardProject(_filesystem, projectPath, sw) is { } guardFailure) {
            return guardFailure;
        }

        try {
            var result = await _analysis.GetDiagnosticsAsync(projectPath, severity, cancellationToken);
            var summary = result.Diagnostics.Count == 0
                ? "No compiler diagnostics."
                : $"Found {result.Diagnostics.Count} compiler diagnostic(s).";
            return ToolResults.Ok(summary, result, sw, result.Warnings);
        } catch (Exception ex) {
            return ToolResults.FromException(ex, "Failed to get compiler diagnostics.", sw);
        }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/UiPath.Engineering.Mcp.Tools.Tests --filter "FullyQualifiedName~CodeAnalysisToolsTests"`
Expected: PASS (10 tests).

- [ ] **Step 5: Commit**

```bash
git add src/UiPath.Engineering.Mcp.Tools/FindCodeReferencesTool.cs src/UiPath.Engineering.Mcp.Tools/GetCompileErrorsTool.cs tests/UiPath.Engineering.Mcp.Tools.Tests/CodeAnalysisToolsTests.cs
git commit -m "feat: add find_code_references and get_compile_errors MCP tools"
```

---

### Task 11: `CompileProjectTool` (UiPath CLI build wrapper)

**Files:**
- Create: `src/UiPath.Engineering.Mcp.Tools/CompileProjectTool.cs`
- Test: `tests/UiPath.Engineering.Mcp.Tools.Tests/CompileProjectToolTests.cs`

**Interfaces:**
- Consumes: `IUiPathCliProvider.ValidateAsync(projectPath, validate, build, pack, ct)` → `UiPathCliResult` (`.Success`, `.Summary`, `.Build` (`CliStepResult`: `Executed`, `Success`, `Errors`, `Warnings`), `.Errors`, `.Warnings`), `FakeUiPathCliProvider` (existing, tracks `LastValidateFlags`).
- Produces: MCP tool `compile_project`.

- [ ] **Step 1: Write the failing tests**

Create `tests/UiPath.Engineering.Mcp.Tools.Tests/CompileProjectToolTests.cs`:

```csharp
namespace UiPath.Engineering.Mcp.Tools.Tests;

public class CompileProjectToolTests {
    private static FakeFilesystemProvider ProjectFilesystem() =>
        new() { Allowed = true, ProjectJson = "/projects/testProcess/project.json" };

    [Fact]
    public async Task CompileProject_PathNotAllowed_ReturnsError() {
        var tool = new CompileProjectTool(new FakeUiPathCliProvider(), new FakeFilesystemProvider { Allowed = false });

        var result = await tool.CompileProject("/not/allowed");

        Assert.Equal("error", result.Status);
        Assert.Equal("Path not allowed.", result.Summary);
    }

    [Fact]
    public async Task CompileProject_HappyPath_RunsBuildOnly() {
        var cli = new FakeUiPathCliProvider();
        var tool = new CompileProjectTool(cli, ProjectFilesystem());

        var result = await tool.CompileProject("/projects/testProcess");

        Assert.Equal((false, true, false), cli.LastValidateFlags);
        Assert.Equal("success", result.Status);
    }

    [Fact]
    public async Task CompileProject_CliFails_ReturnsErrorStatus() {
        var cli = new FakeUiPathCliProvider {
            Result = new() { Success = false, Summary = "Build failed.", Errors = ["error CS0103"] }
        };
        var tool = new CompileProjectTool(cli, ProjectFilesystem());

        var result = await tool.CompileProject("/projects/testProcess");

        Assert.Equal("error", result.Status);
        Assert.Contains("error CS0103", result.Errors);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/UiPath.Engineering.Mcp.Tools.Tests --filter "FullyQualifiedName~CompileProjectToolTests"`
Expected: FAIL — `CompileProjectTool` does not exist (compile error).

- [ ] **Step 3: Implement the tool**

Create `src/UiPath.Engineering.Mcp.Tools/CompileProjectTool.cs`:

```csharp
using System.ComponentModel;
using System.Diagnostics;
using ModelContextProtocol.Server;
using UiPath.Engineering.Mcp.Core.Abstractions;
using UiPath.Engineering.Mcp.Core.Models;
using UiPath.Engineering.Mcp.Providers.UiPathCli;

namespace UiPath.Engineering.Mcp.Tools;

[McpServerToolType]
public sealed class CompileProjectTool {
    private readonly IUiPathCliProvider _cliProvider;
    private readonly IFilesystemProvider _filesystem;

    public CompileProjectTool(IUiPathCliProvider cliProvider, IFilesystemProvider filesystem) {
        _cliProvider = cliProvider;
        _filesystem = filesystem;
    }

    [McpServerTool, Description("Compiles a UiPath project using the authoritative UiPath CLI build step (uip rpa build) and returns structured compiler errors and warnings. Slower than get_compile_errors but is the ground-truth build. Requires the UiPath CLI on the host.")]
    public async Task<ToolResult> CompileProject(
        [Description("Absolute path to the UiPath project directory.")] string projectPath,
        CancellationToken cancellationToken = default) {
        var sw = Stopwatch.StartNew();

        if (ToolResults.GuardProject(_filesystem, projectPath, sw) is { } guardFailure) {
            return guardFailure;
        }

        try {
            var cliResult = await _cliProvider.ValidateAsync(projectPath, validate: false, build: true, pack: false, cancellationToken);

            return new ToolResult {
                Status = cliResult.Success ? "success" : "error",
                Summary = cliResult.Summary,
                Data = new {
                    success = cliResult.Success,
                    build = new {
                        executed = cliResult.Build.Executed,
                        success = cliResult.Build.Executed && cliResult.Build.Success,
                        errors = cliResult.Build.Errors,
                        warnings = cliResult.Build.Warnings
                    },
                    errors = cliResult.Errors,
                    warnings = cliResult.Warnings
                },
                Errors = cliResult.Errors,
                Warnings = cliResult.Warnings,
                DurationMs = sw.ElapsedMilliseconds
            };
        } catch (Exception ex) {
            return ToolResults.FromException(ex, "Project compilation failed.", sw);
        }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/UiPath.Engineering.Mcp.Tools.Tests --filter "FullyQualifiedName~CompileProjectToolTests"`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add src/UiPath.Engineering.Mcp.Tools/CompileProjectTool.cs tests/UiPath.Engineering.Mcp.Tools.Tests/CompileProjectToolTests.cs
git commit -m "feat: add compile_project MCP tool wrapping uip rpa build"
```

---

### Task 12: DI registration + README + full verification

**Files:**
- Modify: `src/UiPath.Engineering.Mcp.Server/Program.cs`
- Modify: `README.md`
- Modify: `docs/superpowers/plans/2026-08-10-csharp-intelligence.md` (this file — check off completed steps if tracking in-place)

**Interfaces:**
- Consumes: everything from Tasks 1-11.
- Produces: a running server exposing the five new tools.

- [ ] **Step 1: Register services in `Program.cs`**

In `src/UiPath.Engineering.Mcp.Server/Program.cs`, add `using UiPath.Engineering.Mcp.Core.CodeAnalysis;` to the usings, then add after the `ImplementationPlanStore` registration:

```csharp
// C# semantic analysis (Roslyn). The context builder is wrapped in the
// fingerprint cache so compilations are only rebuilt when project files change.
builder.Services.AddSingleton<NuGetReferenceResolver>();
builder.Services.AddSingleton<CSharpContextBuilder>();
builder.Services.AddSingleton<ICSharpContextBuilder>(sp =>
    new CSharpAnalysisCache(
        sp.GetRequiredService<CSharpContextBuilder>(),
        sp.GetRequiredService<IFilesystemProvider>()));
builder.Services.AddSingleton<ICSharpAnalysisService, CSharpAnalysisService>();
```

- [ ] **Step 2: Build the solution**

Run: `dotnet build`
Expected: 0 errors.

- [ ] **Step 3: Run the full test suite**

Run: `dotnet test`
Expected: PASS across all three test projects (Core, Providers, Tools).

- [ ] **Step 4: Smoke-test the server exposes the tools**

Run the server in the background:

```bash
dotnet run --project src/UiPath.Engineering.Mcp.Server &
sleep 5
curl -s http://localhost:5000/health
```

Expected: `Healthy`. Then verify tool listing with the MCP Inspector (`npx @modelcontextprotocol/inspector` → connect to `http://localhost:5000/sse`) and confirm `find_code_symbol`, `find_code_references`, `get_code_context`, `get_compile_errors`, `compile_project` are listed. If available, call `find_code_symbol` against the primary test project (`C:\Users\arauj\OneDrive\Documentos\UiPath\testProcess`) with a known method name and confirm `analysisMode` is present in the response. Stop the background server afterwards (`kill %1` / close the job).

- [ ] **Step 5: Update README.md**

Add the five tools to the tool table in `README.md`:

```markdown
| `find_code_symbol` | Finds C# symbols (methods, classes, properties, fields, interfaces) by exact name using Roslyn semantic analysis; returns kind, file, line, containing type, signature. |
| `find_code_references` | Finds all usage sites of a C# symbol across the project's `.cs` files (semantic matching with an identifier-matching fallback for external symbols). |
| `get_code_context` | Returns the semantic context of one C# member (located by symbol name or file+line): signature, containing type, called methods, referenced types, and the member's source. |
| `get_compile_errors` | Structured Roslyn compiler diagnostics (file/line/column/code/severity/message) without running a build; responses include `analysisMode` (`full`/`partial`/`syntaxOnly`). |
| `compile_project` | Authoritative UiPath CLI build (`uip rpa build`) returning structured compiler errors/warnings. |
```

Also add the five tool names to the registration list in section 5, and add one bullet under "Notes / known limitations":

```markdown
- The C# analysis tools (`find_code_symbol`, `find_code_references`, `get_code_context`,
  `get_compile_errors`) build a cached in-memory Roslyn compilation per project. When
  NuGet package assemblies cannot be resolved the response reports
  `analysisMode: "partial"` (some references missing — results may be incomplete) or
  `"syntaxOnly"` (NuGet folder unreachable — declaration/name matching only), so the
  client always knows how much to trust the result.
```

- [ ] **Step 6: Commit**

```bash
git add src/UiPath.Engineering.Mcp.Server/Program.cs README.md
git commit -m "feat: wire C# analysis services into DI; document new tools"
```

---

## Acceptance Criteria (maps to spec §11)

- All five tools listed by the MCP Inspector — verified in Task 12 Step 4.
- `find_code_symbol` locates a coded-workflow entry method with file/line/signature — Tasks 5, 9, smoke test.
- `get_compile_errors` returns a structured `CS0103` with correct file/line — Task 8.
- Missing dependency → `analysisMode: "partial"` + named unresolved dependency — Tasks 3, 5, 8.
- Missing NuGet folder → `analysisMode: "syntaxOnly"` — Task 3.
- Cache invalidation on `.cs` change; unchanged project served from cache — Task 4.
- `dotnet test` green across all three test projects — Task 12 Step 3.
