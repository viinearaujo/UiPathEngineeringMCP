namespace UiPath.Engineering.Mcp.Core.Models;

public sealed class WorkflowModel
{
    public string FileName { get; init; } = string.Empty;
    public string FilePath { get; init; } = string.Empty;
    public bool IsMain { get; init; }
}