using System.ComponentModel;
using System.Diagnostics;
using ModelContextProtocol.Server;
using UiPath.Engineering.Mcp.Core;
using UiPath.Engineering.Mcp.Core.Models;
using UiPath.Engineering.Mcp.Providers.Skills;

namespace UiPath.Engineering.Mcp.Tools;

[McpServerToolType]
public sealed class ReadSkillTool {
    private readonly ISkillsProvider _skills;

    public ReadSkillTool(ISkillsProvider skills) {
        _skills = skills;
    }

    [McpServerTool(UseStructuredContent = true), Description("Reads one RPA skill file (SKILL.md by default, or an auxiliary file under that skill). Use list_skills if the name is unknown. This server only serves RPA playbooks (uipath-rpa, guided-implementation-loop), not Maestro/IXP/Agents.")]
    public async Task<ToolResult> ReadSkill(
        [Description("Skill name or directory, e.g. 'uipath-rpa' (case-insensitive).")] string name,
        [Description("Optional file inside the skill directory, e.g. 'references/auth.md'. Defaults to SKILL.md.")] string? file = null,
        CancellationToken cancellationToken = default) {

        var sw = Stopwatch.StartNew();

        var result = await _skills.ReadAsync(name, file, cancellationToken);
        if (!result.Success) {
            return ToolResults.Failure(result.ErrorMessage ?? "Skill read failed.", [MapError(result)], sw);
        }

        var (redacted, redactedCount) = SecretRedactor.Redact(result.Content);
        return ToolResults.Ok($"Read '{result.File}' from skill '{result.SkillName}'.",
            new {
                name = result.SkillName,
                file = result.File,
                content = redacted,
                truncated = result.Truncated,
                redactedCount
            }, sw);
    }

    private static ToolError MapError(SkillReadResult result) => result.ErrorCode switch {
        "SKILL_NOT_FOUND" => new ToolError(ToolErrorCodes.SkillNotFound, result.ErrorMessage!,
            $"Pick one of the available skills: {string.Join(", ", result.AvailableSkills)}.", "list_skills"),
        "SKILL_PATH_REJECTED" => new ToolError(ToolErrorCodes.SkillPathRejected, result.ErrorMessage!,
            "Pass a file path inside the skill directory, without '..' or absolute paths."),
        "SKILL_FILE_NOT_FOUND" => new ToolError(ToolErrorCodes.SkillFileNotFound, result.ErrorMessage!,
            "Check the file name against the skill directory contents; default is SKILL.md."),
        _ => new ToolError(ToolErrorCodes.SkillsRootMissing, result.ErrorMessage!,
            "Set Skills:SkillsRoot in appsettings.json to a directory containing */SKILL.md.")
    };
}
