namespace UiPath.Engineering.Mcp.Core.Configuration;
public sealed class SkillsOptions {
    // Relative paths are resolved by walking up from the server's working
    // directory (then the app base directory) until the path exists.
    public string SkillsRoot { get; init; } = ".agents/skills";
    // Character cap applied to any single skill file read.
    public int MaxSkillFileBytes { get; init; } = 65536;
}
