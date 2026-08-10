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
