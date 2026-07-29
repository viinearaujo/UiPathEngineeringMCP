namespace UiPath.Engineering.Mcp.Core.Configuration;
public sealed class SkillsOptions {
    // Resolved against the server working directory when relative.
    public string SkillsRoot { get; init; } = ".agents/skills";
    // Character cap applied to any single skill file read.
    public int MaxSkillFileBytes { get; init; } = 65536;
}
