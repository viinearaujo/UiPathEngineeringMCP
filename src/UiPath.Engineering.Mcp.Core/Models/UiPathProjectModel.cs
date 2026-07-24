namespace UiPath.Engineering.Mcp.Core.Models;
public sealed class UiPathProjectModel {
    public string ProjectPath { get; init; } = string.Empty;
    public string ProjectName { get; init; } = string.Empty;
    public string? MainWorkflow { get; init; }
    public string? ProjectJsonPath { get; init; }
    public List<string> Workflows { get; init; } = [];
    public List<string> Dependencies { get; init; } = [];
    public List<string> Risks { get; init; } = [];
}