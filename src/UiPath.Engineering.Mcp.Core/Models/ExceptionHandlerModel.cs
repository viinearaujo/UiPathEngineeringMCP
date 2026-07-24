namespace UiPath.Engineering.Mcp.Core.Models;

public sealed class ExceptionHandlerModel
{
    public string WorkflowName { get; init; } = string.Empty;
    public bool HasGlobalHandler { get; init; }
    public List<string> CatchTypes { get; init; } = [];
}