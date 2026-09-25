namespace UiPath.Engineering.Mcp.Providers.Tests;

// list_skills and read_skill scan every directory under SkillsRoot that contains SKILL.md.
// The remote tree is RPA-only. A `uip skills install` of the marketplace catalog would
// advertise Maestro, IXP, Agents, and the rest again, so this test names the only two
// playbooks this server serves.
public class SkillsRootAllowlistTests {
    private static readonly string[] AllowedSkillDirectories = [
        "guided-implementation-loop",
        "uipath-rpa",
    ];

    [Fact]
    public void SkillsRoot_ContainsOnlyRpaPlaybooks() {
        if (FindRepoRoot() is not { } repoRoot) {
            return;
        }

        var skillsRoot = Path.Combine(repoRoot, ".agents", "skills");
        if (!Directory.Exists(skillsRoot)) {
            return;
        }

        var installed = Directory.EnumerateDirectories(skillsRoot)
            .Where(dir => File.Exists(Path.Combine(dir, "SKILL.md")))
            .Select(Path.GetFileName)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(AllowedSkillDirectories, installed);
    }

    private static string? FindRepoRoot() {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null) {
            if (Directory.Exists(Path.Combine(directory.FullName, ".git"))) {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }
}
