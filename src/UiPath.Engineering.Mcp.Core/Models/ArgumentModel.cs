namespace UiPath.Engineering.Mcp.Core.Models;

public sealed class ArgumentModel {
    public string Name { get; init; } = string.Empty;
    public string Direction { get; init; } = string.Empty; // In, Out, In/Out
    public string Type { get; init; } = string.Empty;
}
