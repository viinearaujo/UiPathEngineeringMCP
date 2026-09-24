namespace UiPath.Engineering.Mcp.Providers.Tests;

// Regression guard for the coded-first local divergence of the vendored uipath-rpa skill.
//
// .agents/skills/uipath-rpa/ is a snapshot installed from the UiPath skills marketplace, which
// ships XAML-first. This server is coded-first, so the snapshot is patched on disk after every
// install. A `uip skills install` / self-update silently restores the upstream XAML-first wording
// and has done so at least once, which is why this test exists.
//
// Source of truth: .agents/skills/LOCAL-DIVERGENCE-uipath-rpa.md (repo-authored, lives OUTSIDE the
// vendored tree so it survives a reinstall). It carries the "Patched locations" table, the
// preserved-upstream rules, and the documented verification command this test mirrors:
//
//   git grep -inE "default to XAML|XAML is the default|mean XAML|Default\*\* → XAML|XAML\*\* \(default\)" -- .agents/skills/uipath-rpa
//
// The only permitted XAML-first statement is references/coded-vs-xaml-guide.md flowchart step 1's
// XAML-only-project rule ("An existing project's mode governs" is a preserved upstream rule).
// Any other hit is an unrepaired regression - re-apply the "Now says" column.
public class LocalDivergenceGuardTests {
    // Regression phrases, transcribed from the documented git grep above (matched case-insensitively).
    private static readonly string[] RegressionPhrases = [
        "default to xaml",
        "xaml is the default",
        "mean xaml",
        "default** → xaml",
        "xaml** (default)",
    ];

    // The sole allowed exception: an existing XAML-only project keeps using XAML (preserved rule).
    private const string AllowedExceptionPrefix = "- **xaml-only project**";

    // The divergence mirror mirrors the work order and therefore necessarily quotes the XAML-first
    // phrases in its "Claim changed" column. It is generated/restored by hand and must never be
    // tracked (tracking it would break the documented grep), so it is excluded from the scan.
    private const string MirrorFileName = "LOCAL-DIVERGENCE.md";

    [Fact]
    public void VendoredSkillTree_DoesNotRevertToXamlFirstWording() {
        if (FindRepoRoot() is not { } repoRoot) {
            return; // Repo root unreachable from the test binary (e.g. packaged run) - nothing to scan.
        }

        var skillRoot = Path.Combine(repoRoot, ".agents", "skills", "uipath-rpa");
        if (!Directory.Exists(skillRoot)) {
            return; // Vendored tree intentionally absent in this configuration.
        }

        var files = Directory.EnumerateFiles(skillRoot, "*.md", SearchOption.AllDirectories)
            .Where(f => !string.Equals(Path.GetFileName(f), MirrorFileName, StringComparison.Ordinal))
            .ToList();
        Assert.True(files.Count > 10, $"Expected to scan the vendored skill tree, found {files.Count} markdown files.");

        var violations = new List<string>();
        foreach (var file in files) {
            var relative = Path.GetRelativePath(skillRoot, file).Replace('\\', '/');
            var lineNumber = 0;
            foreach (var line in File.ReadLines(file)) {
                lineNumber++;
                var normalized = line.Trim().ToLowerInvariant();
                if (normalized.StartsWith(AllowedExceptionPrefix, StringComparison.Ordinal)) {
                    continue;
                }

                if (RegressionPhrases.Any(p => normalized.Contains(p, StringComparison.Ordinal))) {
                    violations.Add($"{relative}:{lineNumber}: {line.Trim()}");
                }
            }
        }

        Assert.True(violations.Count == 0,
            "The vendored uipath-rpa skill reverted to XAML-first wording. Re-apply the \"Now says\" "
            + "column of .agents/skills/LOCAL-DIVERGENCE-uipath-rpa.md. Offending lines:" + Environment.NewLine
            + string.Join(Environment.NewLine, violations));
    }

    // Walks up from the test binary to the directory that holds .git.
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