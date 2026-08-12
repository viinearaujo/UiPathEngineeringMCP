namespace UiPath.Engineering.Mcp.Core.Models;

public sealed class InvokeWorkflowModel {
    public string SourceWorkflow { get; init; } = string.Empty;
    public string TargetWorkflow { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public List<ArgumentMappingModel> ArgumentMappings { get; init; } = [];
}
