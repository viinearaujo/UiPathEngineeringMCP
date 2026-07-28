namespace UiPath.Engineering.Mcp.Core.Models;

public sealed class WorkflowModel
{
    public string FileName { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public bool IsMain { get; set; }
    public bool HasParseError { get; set; }
    public string? ParseError { get; set; }
    public List<ArgumentModel> Arguments { get; init; } = [];
    public List<VariableModel> Variables { get; init; } = [];
    public List<ActivityModel> Activities { get; init; } = [];
    public List<ExceptionHandlerModel> ExceptionHandlers { get; init; } = [];
    public List<InvokeWorkflowModel> InvokeWorkflows { get; init; } = [];
    public List<LogMessageModel> LogMessages { get; init; } = [];
}
