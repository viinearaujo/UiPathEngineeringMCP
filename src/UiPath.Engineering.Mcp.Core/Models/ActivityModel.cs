namespace UiPath.Engineering.Mcp.Core.Models;

public sealed class ActivityModel
{
    public string DisplayName { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty;
    public int Depth { get; init; }
}
