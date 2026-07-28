namespace UiPath.Engineering.Mcp.Core.Models;

public sealed class LogMessageModel
{
    public string DisplayName { get; init; } = string.Empty;
    public string Level { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
}
