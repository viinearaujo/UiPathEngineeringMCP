namespace UiPath.Engineering.Mcp.Core.Models;

public sealed class CodedWorkflowModel {
    public string FileName { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public string ClassName { get; set; } = string.Empty;
    public string Namespace { get; set; } = string.Empty;
    public bool IsCodedWorkflow { get; set; }
    public List<string> EntryMethods { get; init; } = [];
    public List<string> PublicMethods { get; init; } = [];
    public bool HasParseError { get; set; }
    public string? ParseError { get; set; }
}
