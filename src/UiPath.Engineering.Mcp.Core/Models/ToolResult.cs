namespace UiPath.Engineering.Mcp.Core.Models;
public sealed class ToolResult {
    public string Status { get; init; } = "success";
    public string Summary { get; init; } = string.Empty;
    public object? Data { get; init; }
    public List<string> Errors { get; init; } = [];
    public List<ToolError> ErrorDetails { get; init; } = [];
    public List<string> Warnings { get; init; } = [];
    public long DurationMs { get; init; }
}
