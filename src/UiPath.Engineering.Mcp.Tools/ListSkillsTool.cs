using System.ComponentModel;
using System.Diagnostics;
using ModelContextProtocol.Server;
using UiPath.Engineering.Mcp.Core;
using UiPath.Engineering.Mcp.Core.Models;
using UiPath.Engineering.Mcp.Providers.Skills;

namespace UiPath.Engineering.Mcp.Tools;

[McpServerToolType]
public sealed class ListSkillsTool {
    private readonly ISkillsProvider _skills;

    public ListSkillsTool(ISkillsProvider skills) {
        _skills = skills;
    }

    [McpServerTool, Description("Lists the UiPath skills catalog (name + description) — the playbooks for UiPath tasks. Call read_skill with a name from this list to load the full instructions before doing UiPath work.")]
    public async Task<ToolResult> ListSkills(CancellationToken cancellationToken = default) {
        var sw = Stopwatch.StartNew();

        IReadOnlyList<SkillSummary> skills;
        try {
            skills = await _skills.ListAsync(cancellationToken);
        } catch (DirectoryNotFoundException ex) {
            return ToolResults.Failure("Skills root not found.",
                [new ToolError(ToolErrorCodes.SkillsRootMissing, ex.Message,
                    "Set Skills:SkillsRoot in appsettings.json to a directory containing */SKILL.md.")], sw);
        }

        return ToolResults.Ok($"Found {skills.Count} skill(s).",
            new { skills = skills.Select(s => new { s.Name, s.Description, s.Directory }).ToList() }, sw);
    }
}
