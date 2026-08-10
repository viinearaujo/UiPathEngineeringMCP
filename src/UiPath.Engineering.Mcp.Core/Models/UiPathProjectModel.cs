namespace UiPath.Engineering.Mcp.Core.Models;
public sealed class UiPathProjectModel {
    public string ProjectPath { get; init; } = string.Empty;
    public string ProjectName { get; init; } = string.Empty;
    public string? MainWorkflow { get; init; }
    public List<string> EntryPoints { get; init; } = [];
    public string? ProjectJsonPath { get; init; }
    public string? Description { get; set; }
    public string? TargetFramework { get; init; }
    public string? ReadmeSummary { get; set; }
    public DirectoryTreeNode? FolderStructure { get; set; }
    public List<WorkflowModel> Workflows { get; init; } = [];
    public List<CodedWorkflowModel> CodedWorkflows { get; init; } = [];
    public List<PackageModel> Packages { get; init; } = [];
    public List<VariableModel> Variables { get; init; } = [];
    public List<ArgumentModel> Arguments { get; init; } = [];
    public List<InvokeWorkflowModel> InvokeWorkflows { get; init; } = [];
    public List<ExceptionHandlerModel> ExceptionHandlers { get; init; } = [];
    public List<string> Dependencies { get; init; } = [];
    public List<string> Risks { get; init; } = [];
}
