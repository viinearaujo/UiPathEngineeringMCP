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
