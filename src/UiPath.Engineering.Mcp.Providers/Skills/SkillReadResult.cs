namespace UiPath.Engineering.Mcp.Providers.Skills;
public sealed class SkillReadResult {
    public bool Success { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public string SkillName { get; init; } = string.Empty;
    public string File { get; init; } = string.Empty;
    public string Content { get; init; } = string.Empty;
    public bool Truncated { get; init; }
    public IReadOnlyList<string> AvailableSkills { get; init; } = [];
}
