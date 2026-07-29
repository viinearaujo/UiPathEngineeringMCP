namespace UiPath.Engineering.Mcp.Core.Models;

public sealed record ToolError(string ErrorCode, string Message, string FixHint, string? SuggestedTool = null);
