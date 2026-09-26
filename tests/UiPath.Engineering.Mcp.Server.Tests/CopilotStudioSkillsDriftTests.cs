using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using UiPath.Engineering.Mcp.Core.Configuration;
using UiPath.Engineering.Mcp.Tools;

namespace UiPath.Engineering.Mcp.Server.Tests;

/// <summary>
/// The skills under docs/copilot-studio-skills are uploaded to Copilot Studio, which only sees the
/// default connector. A tool name outside DefaultNames, or a reference file that neither ships in
/// the zip (pack.json) nor exists in the vendored playbook, is a failed call at runtime.
/// </summary>
public class CopilotStudioSkillsDriftTests {
    // Tools removed from the server. Docs that still name them route Copilot to nothing.
    private static readonly string[] RetiredToolNames = [
        "read_skill",
        "find_code_symbol",
        "get_code_context",
        "find_code_references",
        "manage_project_docs",
        "manage_project_file",
        "sync_project_context",
        "validate_project_docs",
        "search_activity_docs",
        "search_uipath_knowledge",
    ];

    // The canvas skeleton prompt (section 5.1) enables this leave-off tool for a single call.
    private static readonly string[] PromptPackLeaveOffAllowed = ["generate_documentation"];

    private static readonly string[] ActivitySpecSkills = ["rpa-authoring", "guided-implementation-loop"];

    private static readonly Regex Token = new("[a-z][a-z0-9_]*", RegexOptions.Compiled);
    private static readonly Regex ReferencePath = new(@"references/[A-Za-z0-9_\-/]+\.md", RegexOptions.Compiled);
    private static readonly Regex SkillNamePattern = new("^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.Compiled);

    [Fact]
    public void Skills_NameOnlyDefaultConnectorTools() {
        var root = FindRepoRoot();
        var known = CopilotToolCatalogTests.ListMcpToolNames(Assembly.Load("UiPath.Engineering.Mcp.Tools"))
            .Concat(RetiredToolNames)
            .ToHashSet(StringComparer.Ordinal);

        var failures = new List<string>();
        foreach (var (skill, text) in ReadSkills(root)) {
            failures.AddRange(ToolTokens(text, known)
                .Where(name => !CopilotConnectorTools.IsDefault(name))
                .Select(name => $"{skill}: {name}"));
        }

        Assert.True(
            failures.Count == 0,
            "Copilot Studio skills may only name CopilotConnectorTools.DefaultNames. Found: "
            + string.Join(", ", failures));
    }

    [Fact]
    public void Skills_FrontmatterNameMatchesFolder_AndDescriptionFits() {
        var root = FindRepoRoot();
        var failures = new List<string>();
        foreach (var (skill, text) in ReadSkills(root)) {
            var name = FrontmatterValue(text, "name");
            var description = FrontmatterValue(text, "description");

            if (!string.Equals(name, skill, StringComparison.Ordinal)) {
                failures.Add($"{skill}: frontmatter name '{name}' must equal the folder name");
            }

            if (name is null || name.Length > 64 || !SkillNamePattern.IsMatch(name)) {
                failures.Add($"{skill}: name must be lowercase letters, digits, and single hyphens (max 64)");
            }

            if (string.IsNullOrWhiteSpace(description) || description.Length > 1024) {
                failures.Add($"{skill}: description must be 1-1024 characters (was {description?.Length ?? 0})");
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public void Skills_ReferencePathsResolve_ToBundledOrVendoredFiles() {
        var root = FindRepoRoot();
        var bundled = ReadPackIncludes(root);
        var vendored = Path.Combine(root, ".agents", "skills", "uipath-rpa");

        var failures = new List<string>();
        foreach (var (skill, text) in ReadSkills(root)) {
            var skillBundle = bundled.TryGetValue(skill, out var includes)
                ? includes.Select(i => i.To).ToHashSet(StringComparer.Ordinal)
                : [];

            foreach (var reference in ReferencePath.Matches(text).Select(m => m.Value).Distinct(StringComparer.Ordinal)) {
                if (skillBundle.Contains(reference) || File.Exists(Path.Combine(vendored, reference))) {
                    continue;
                }

                failures.Add($"{skill}: {reference}");
            }
        }

        Assert.True(
            failures.Count == 0,
            "Each references/... path must ship in the skill zip (pack.json) or exist under .agents/skills/uipath-rpa. Missing: "
            + string.Join(", ", failures));
    }

    [Fact]
    public void PackManifest_NamesRealSkillsAndSources() {
        var root = FindRepoRoot();
        var skillsDir = Path.Combine(root, "docs", "copilot-studio-skills");
        var failures = new List<string>();

        foreach (var (skill, includes) in ReadPackIncludes(root)) {
            if (!File.Exists(Path.Combine(skillsDir, skill, "SKILL.md"))) {
                failures.Add($"{skill}: no docs/copilot-studio-skills/{skill}/SKILL.md");
            }

            foreach (var include in includes) {
                if (!File.Exists(Path.Combine(root, include.From))) {
                    failures.Add($"{skill}: source '{include.From}' does not exist");
                }

                if (include.To.StartsWith('/') || include.To.Split('/').Contains("..")
                    || string.Equals(include.To, "SKILL.md", StringComparison.OrdinalIgnoreCase)) {
                    failures.Add($"{skill}: target '{include.To}' must be a relative path other than SKILL.md");
                }
            }

            var duplicates = includes.GroupBy(i => i.To, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key);
            failures.AddRange(duplicates.Select(to => $"{skill}: '{to}' is packed twice"));
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public void AuthoringSkills_PackTheGrammarTheResourceServes_AndEveryIdiom() {
        var root = FindRepoRoot();
        var grammarPath = Path.Combine(root, "docs", "authoring", "activity-spec.md");
        Assert.Equal(
            NormalizeNewlines(File.ReadAllText(grammarPath)),
            NormalizeNewlines(new AuthoringResources().GetActivitySpecGuide()));

        var idioms = Directory.EnumerateFiles(Path.Combine(root, "docs", "copilot-idioms"), "*.md")
            .Select(Path.GetFileName)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        Assert.NotEmpty(idioms);

        var packed = ReadPackIncludes(root);
        foreach (var skill in ActivitySpecSkills) {
            var includes = Assert.Contains(skill, packed);
            Assert.Contains(includes, i =>
                string.Equals(i.From, "docs/authoring/activity-spec.md", StringComparison.Ordinal)
                && string.Equals(i.To, "references/activity-spec.md", StringComparison.Ordinal));

            foreach (var idiom in idioms) {
                Assert.Contains(includes, i =>
                    string.Equals(i.From, $"docs/copilot-idioms/{idiom}", StringComparison.Ordinal)
                    && string.Equals(i.To, $"references/idioms/{idiom}", StringComparison.Ordinal));
            }
        }
    }

    [Fact]
    public void PromptPackAndConnectionDoc_NameNoRetiredTools() {
        var root = FindRepoRoot();
        var retired = RetiredToolNames.ToHashSet(StringComparer.Ordinal);
        var registered = CopilotToolCatalogTests.ListMcpToolNames(Assembly.Load("UiPath.Engineering.Mcp.Tools"))
            .ToHashSet(StringComparer.Ordinal);

        var prompts = File.ReadAllText(Path.Combine(root, "docs", "copilot-prompts.md"));
        var connection = File.ReadAllText(Path.Combine(root, "docs", "agent-connection.md"));

        Assert.Empty(ToolTokens(prompts, retired));
        Assert.Empty(ToolTokens(connection, retired));

        var promptLeaveOff = ToolTokens(prompts, registered)
            .Where(name => !CopilotConnectorTools.IsDefault(name))
            .Where(name => !PromptPackLeaveOffAllowed.Contains(name, StringComparer.Ordinal))
            .ToArray();
        Assert.True(
            promptLeaveOff.Length == 0,
            "The prompt pack is pasted into Copilot, which only sees the default connector. Leave-off tools named: "
            + string.Join(", ", promptLeaveOff));
    }

    private static IEnumerable<(string Skill, string Text)> ReadSkills(string root) {
        var skillsDir = Path.Combine(root, "docs", "copilot-studio-skills");
        var skills = Directory.EnumerateDirectories(skillsDir)
            .Where(dir => File.Exists(Path.Combine(dir, "SKILL.md")))
            .OrderBy(dir => dir, StringComparer.Ordinal)
            .Select(dir => (Path.GetFileName(dir), File.ReadAllText(Path.Combine(dir, "SKILL.md"))))
            .ToList();

        Assert.NotEmpty(skills);
        return skills;
    }

    private static string[] ToolTokens(string text, IReadOnlySet<string> names) =>
        Token.Matches(text)
            .Select(m => m.Value)
            .Where(names.Contains)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

    private static string? FrontmatterValue(string text, string key) {
        var lines = NormalizeNewlines(text).Split('\n');
        if (lines.Length == 0 || lines[0] != "---") {
            return null;
        }

        foreach (var line in lines.Skip(1).TakeWhile(l => l != "---")) {
            var prefix = key + ":";
            if (line.StartsWith(prefix, StringComparison.Ordinal)) {
                return line[prefix.Length..].Trim().Trim('"', '\'');
            }
        }

        return null;
    }

    private static Dictionary<string, List<PackInclude>> ReadPackIncludes(string root) {
        using var document = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(root, "docs", "copilot-studio-skills", "pack.json")));

        var result = new Dictionary<string, List<PackInclude>>(StringComparer.Ordinal);
        foreach (var skill in document.RootElement.GetProperty("skills").EnumerateArray()) {
            var name = skill.GetProperty("name").GetString() ?? string.Empty;
            var includes = skill.TryGetProperty("include", out var include)
                ? include.EnumerateArray()
                    .Select(i => new PackInclude(
                        i.GetProperty("from").GetString() ?? string.Empty,
                        i.GetProperty("to").GetString() ?? string.Empty))
                    .ToList()
                : [];
            result.Add(name, includes);
        }

        return result;
    }

    private static string NormalizeNewlines(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);

    private static string FindRepoRoot() {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null) {
            if (File.Exists(Path.Combine(directory.FullName, "UiPath.Engineering.Mcp.sln"))) {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root (UiPath.Engineering.Mcp.sln).");
    }

    private sealed record PackInclude(string From, string To);
}
