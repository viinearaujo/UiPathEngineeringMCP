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

    // --- generated coded-workflow sources (.local/.codedworkflows) --------------
    //
    // These use a real temp directory because discovery bypasses IFilesystemProvider by
    // design: `.local` stays on the provider ignore list so generated code never reaches
    // search, the project model, gap analysis or authoring.

    /// <summary>
    /// Stands in for the Studio-generated <c>ObjectRepository.cs</c>: the
    /// <c>Descriptors.&lt;App&gt;.&lt;Screen&gt;.&lt;Element&gt;</c> accessor surface.
    /// BCL types only, so a clean compile is provable without activity packages.
    /// </summary>
    private const string GeneratedObjectRepository = """
        namespace TestProcess;

        public static class Descriptors {
            public static class MyApp {
                public static class Login {
                    public static string Screen => "<html app='login' />";
                    public static string UsernameField => "<webctrl id='user' />";
                }
            }
        }
        """;

    private const string UiCodedWorkflowSource = """
        namespace TestProcess;

        public class LoginFlow {
            public string TargetSelector() => Descriptors.MyApp.Login.UsernameField;
        }
        """;

    /// <summary>
    /// A project on real disk whose authored source references descriptors. The fake
    /// filesystem serves project.json and the authored .cs (keyed by real path) exactly as
    /// production's FilesystemProvider would — i.e. never returning anything under `.local`.
    /// </summary>
    private sealed class DescriptorProject : IDisposable {
        public string Root { get; } = Path.Combine(
            Path.GetTempPath(), "ctx-descriptors-" + Guid.NewGuid().ToString("N"));

        public string AuthoredCs { get; }

        public string GeneratedFolder => Path.Combine(Root, ".local", ".codedworkflows");

        public FakeFilesystemProvider Filesystem { get; } = new();

        public DescriptorProject() {
            Directory.CreateDirectory(Root);
            AuthoredCs = Path.Combine(Root, "LoginFlow.cs");
            File.WriteAllText(Path.Combine(Root, "project.json"), ProjectJson);
            File.WriteAllText(AuthoredCs, UiCodedWorkflowSource);

            Filesystem.ProjectJsonPath = Path.Combine(Root, "project.json");
            Filesystem.FileContents[Filesystem.ProjectJsonPath] = ProjectJson;
            Filesystem.FileContents[AuthoredCs] = UiCodedWorkflowSource;
            Filesystem.CSharpFiles.Add(AuthoredCs);
        }

        private const string ProjectJson = """{ "name": "testProcess", "targetFramework": "net8.0", "dependencies": {} }""";

        public void AddGeneratedObjectRepository() {
            Directory.CreateDirectory(GeneratedFolder);
            File.WriteAllText(Path.Combine(GeneratedFolder, "ObjectRepository.cs"), GeneratedObjectRepository);
        }

        public string GeneratedObjectRepositoryPath => Path.Combine(GeneratedFolder, "ObjectRepository.cs");

        public CSharpContextBuilder CreateSut() =>
            new(Filesystem, new NuGetReferenceResolver("/nonexistent-nuget-folder"));

        public void Dispose() {
            try { Directory.Delete(Root, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task BuildAsync_GeneratedObjectRepositoryPresent_DescriptorReferencesCompileClean() {
        using var project = new DescriptorProject();
        project.AddGeneratedObjectRepository();

        var context = await project.CreateSut().BuildAsync(project.Root);

        // The whole point: no bogus CS0103/CS0246 for `Descriptors.MyApp.Login.UsernameField`.
        Assert.Empty(context.Compilation.GetDiagnostics().Where(d => d.Id is "CS0103" or "CS0246"));
        Assert.Empty(context.Warnings);
        Assert.Equal(CSharpAnalysisMode.Full, context.Mode);
        Assert.True(context.HasCSharpFiles);
        Assert.Contains(context.Compilation.SyntaxTrees, t =>
            string.Equals(t.FilePath, project.GeneratedObjectRepositoryPath, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task BuildAsync_GeneratedObjectRepositoryPresent_ExposesDescriptorSymbols() {
        using var project = new DescriptorProject();
        project.AddGeneratedObjectRepository();

        var context = await project.CreateSut().BuildAsync(project.Root);

        var descriptors = context.Compilation.GetTypeByMetadataName("TestProcess.Descriptors");
        Assert.NotNull(descriptors);
        Assert.NotNull(descriptors!.GetTypeMembers("MyApp").SingleOrDefault()?.GetTypeMembers("Login"));
    }

    [Fact]
    public async Task BuildAsync_GeneratedObjectRepositoryAbsent_BehaviorUnchanged() {
        using var project = new DescriptorProject();

        var context = await project.CreateSut().BuildAsync(project.Root);

        // Degradation honesty: the descriptor is genuinely unresolvable, so it is reported —
        // and nothing else changes (no new warnings, same tree set, same mode).
        Assert.Contains(context.Compilation.GetDiagnostics(), d => d.Id == "CS0103");
        Assert.Empty(context.Warnings);
        Assert.Equal(CSharpAnalysisMode.Full, context.Mode);
        Assert.True(context.HasCSharpFiles);
        Assert.Equal(
            [project.AuthoredCs],
            context.Compilation.SyntaxTrees.Select(t => t.FilePath));
    }

    [Fact]
    public async Task BuildAsync_GeneratedFolderHoldsNonCSharpFiles_OnlyCsFilesAreCompiled() {
        using var project = new DescriptorProject();
        project.AddGeneratedObjectRepository();
        Directory.CreateDirectory(Path.Combine(project.GeneratedFolder, "nested"));
        File.WriteAllText(Path.Combine(project.GeneratedFolder, "notes.txt"), "not source");
        File.WriteAllText(Path.Combine(project.GeneratedFolder, "nested", "Deep.cs"), "class Deep { }");

        var context = await project.CreateSut().BuildAsync(project.Root);

        Assert.Empty(context.Warnings);
        Assert.DoesNotContain(context.Compilation.SyntaxTrees, t => t.FilePath.EndsWith(".txt", StringComparison.Ordinal));
        Assert.DoesNotContain(context.Compilation.SyntaxTrees, t => t.FilePath.Contains("nested", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task BuildAsync_MalformedGeneratedFile_IsSurfacedAsDiagnosticsNotACrash() {
        // A half-written ObjectRepository.cs (interrupted Studio regen) must degrade to
        // compiler diagnostics like any other source file, never to a thrown build.
        using var project = new DescriptorProject();
        Directory.CreateDirectory(project.GeneratedFolder);
        File.WriteAllText(Path.Combine(project.GeneratedFolder, "ObjectRepository.cs"), "class Truncated {");

        var context = await project.CreateSut().BuildAsync(project.Root);

        Assert.Empty(context.Warnings);
        Assert.Contains(
            context.Compilation.GetDiagnostics(),
            d => d.Location.GetLineSpan().Path.Contains("ObjectRepository.cs", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task BuildAsync_GeneratedFileNameCollidesWithAuthored_PrefersAuthoredCopy() {
        // A stale generated ObjectRepository.cs alongside an authored file of the same name
        // must not introduce a CS0101 duplicate-definition error that the project did not
        // have before generated sources were included.
        using var project = new DescriptorProject();
        var authored = Path.Combine(project.Root, "ObjectRepository.cs");
        File.WriteAllText(authored, """
            namespace TestProcess;
            public static class Descriptors { public static class Mine { public static string Screen => "authored"; } }
            """);
        project.Filesystem.FileContents[authored] = File.ReadAllText(authored);
        project.Filesystem.CSharpFiles.Add(authored);
        project.AddGeneratedObjectRepository();

        var context = await project.CreateSut().BuildAsync(project.Root);

        Assert.DoesNotContain(context.Compilation.GetDiagnostics(), d => d.Id == "CS0101");
        Assert.Equal(
            [project.AuthoredCs, authored],
            context.Compilation.SyntaxTrees.Select(t => t.FilePath));
        Assert.NotNull(context.Compilation.GetTypeByMetadataName("TestProcess.Descriptors")!.GetTypeMembers("Mine").SingleOrDefault());
    }

    [Fact]
    public void FindGeneratedCodedFiles_FolderAbsent_ReturnsEmpty() {
        using var project = new DescriptorProject();

        Assert.Empty(CSharpContextBuilder.FindGeneratedCodedFiles(project.Root));
    }

    [Fact]
    public void FindGeneratedCodedFiles_NonexistentProjectPath_ReturnsEmptyWithoutThrowing() {
        Assert.Empty(CSharpContextBuilder.FindGeneratedCodedFiles(Root));
    }

    [Fact]
    public void FindGeneratedCodedFiles_CodedWorkflowsIsAFile_ReturnsEmptyWithoutThrowing() {
        using var project = new DescriptorProject();
        Directory.CreateDirectory(Path.Combine(project.Root, ".local"));
        File.WriteAllText(project.GeneratedFolder, "not a directory");

        Assert.Empty(CSharpContextBuilder.FindGeneratedCodedFiles(project.Root));
    }

    [Fact]
    public void FindGeneratedCodedFiles_GeneratedSourcesPresent_ReturnsThemSorted() {
        using var project = new DescriptorProject();
        project.AddGeneratedObjectRepository();
        File.WriteAllText(Path.Combine(project.GeneratedFolder, "CodedWorkflow.cs"), "class Generated { }");

        var found = CSharpContextBuilder.FindGeneratedCodedFiles(project.Root);

        Assert.Equal(
            ["CodedWorkflow.cs", "ObjectRepository.cs"],
            found.Select(Path.GetFileName));
        // Sandbox: every discovered path must stay inside the project root.
        Assert.All(found, path => Assert.True(PathPolicy.IsWithin(project.Root, path, allowEqual: false)));
    }
}
