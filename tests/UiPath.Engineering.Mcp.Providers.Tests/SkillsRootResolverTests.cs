using UiPath.Engineering.Mcp.Providers.Skills;

namespace UiPath.Engineering.Mcp.Providers.Tests;

public class SkillsRootResolverTests : IDisposable {
    private readonly string _root = Path.Combine(Path.GetTempPath(), "skillsroot-resolver-" + Guid.NewGuid().ToString("N"));

    public SkillsRootResolverTests() {
        Directory.CreateDirectory(Path.Combine(_root, ".agents", "skills", "uipath-rpa"));
        Directory.CreateDirectory(Path.Combine(_root, "src", "UiPath.Engineering.Mcp.Server"));
    }

    public void Dispose() {
        if (Directory.Exists(_root)) {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void Resolve_AbsolutePath_ReturnsItNormalized() {
        var absolute = Path.Combine(_root, "somewhere", "..", ".agents", "skills");

        var resolved = SkillsRootResolver.Resolve(absolute, Path.Combine(_root, "src"));

        Assert.Equal(Path.GetFullPath(Path.Combine(_root, ".agents", "skills")), resolved);
    }

    [Fact]
    public void Resolve_RelativePath_FoundByWalkingUpFromStartDirectory() {
        var serverDir = Path.Combine(_root, "src", "UiPath.Engineering.Mcp.Server");

        var resolved = SkillsRootResolver.Resolve(".agents/skills", serverDir);

        Assert.Equal(Path.GetFullPath(Path.Combine(_root, ".agents", "skills")), resolved);
    }

    [Fact]
    public void Resolve_RelativePath_FoundDirectlyUnderStartDirectory() {
        var resolved = SkillsRootResolver.Resolve(".agents/skills", _root);

        Assert.Equal(Path.GetFullPath(Path.Combine(_root, ".agents", "skills")), resolved);
    }

    [Fact]
    public void Resolve_RelativePath_NotFound_FallsBackToStartDirectoryCombination() {
        var serverDir = Path.Combine(_root, "src", "UiPath.Engineering.Mcp.Server");

        var resolved = SkillsRootResolver.Resolve("no/such/dir", serverDir);

        Assert.Equal(Path.GetFullPath(Path.Combine(serverDir, "no", "such", "dir")), resolved);
    }
}
