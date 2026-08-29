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

    [McpServerTool(UseStructuredContent = true), Description("Lists RPA playbooks served by this MCP (name + short description). This is not a full UiPath product catalog — Maestro, IXP, Insights, and Agents are omitted. Call read_skill for one name (uipath-rpa or guided-implementation-loop) before implement work.")]
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
