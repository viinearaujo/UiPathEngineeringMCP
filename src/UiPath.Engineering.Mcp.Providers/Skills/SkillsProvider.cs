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

    private string ResolvedRoot {
        get {
            var resolved = SkillsRootResolver.Resolve(_options.SkillsRoot, Directory.GetCurrentDirectory());
            if (!Path.IsPathRooted(_options.SkillsRoot) && !Directory.Exists(resolved)) {
                resolved = SkillsRootResolver.Resolve(_options.SkillsRoot, AppContext.BaseDirectory);
            }
            return resolved;
        }
    }

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
