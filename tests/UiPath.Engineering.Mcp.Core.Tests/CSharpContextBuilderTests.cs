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
