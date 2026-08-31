namespace UiPath.Engineering.Mcp.Core.Models;

/// <summary>
/// One argument binding on an InvokeWorkflowFile: which argument of the target
/// workflow is wired, in which direction, and the binding expression from the caller.
/// </summary>
public sealed class ArgumentMappingModel {
    public string Direction { get; init; } = string.Empty; // In, Out, In/Out
    public string TargetArgument { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty;
    public string Expression { get; init; } = string.Empty;
}
