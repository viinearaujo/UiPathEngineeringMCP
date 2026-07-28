namespace UiPath.Engineering.Mcp.Core.Models;

public sealed class VariableModel {
    public string Name { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty;
    public string? DefaultValue { get; init; }
    public string? Scope { get; init; }
}
