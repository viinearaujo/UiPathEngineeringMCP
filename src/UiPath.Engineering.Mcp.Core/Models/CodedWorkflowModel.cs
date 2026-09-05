namespace UiPath.Engineering.Mcp.Core.Models;

public sealed class CodedWorkflowModel {
    public string FileName { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public string ClassName { get; set; } = string.Empty;
    public string Namespace { get; set; } = string.Empty;
    public bool IsCodedWorkflow { get; set; }
    /// <summary>
    /// One of <see cref="CodedFileKind.Workflow"/>, <see cref="CodedFileKind.Test"/>,
    /// or <see cref="CodedFileKind.Source"/>.
    /// </summary>
    public string Kind { get; set; } = CodedFileKind.Source;
    public List<string> EntryMethods { get; init; } = [];
    public List<ArgumentModel> EntryArguments { get; init; } = [];
    public List<string> PublicMethods { get; init; } = [];
    /// <summary>
    /// True when every <c>[Workflow]</c> entry method body contains try/catch.
    /// Null when the file was not body-scanned (constructed models, source/test).
    /// </summary>
    public bool? EntryHasTryCatch { get; set; }
    /// <summary>
    /// True when any <c>[Workflow]</c> entry method body calls <c>Log(</c>,
    /// <c>LogMessage</c>, or <c>log.</c>. Null when the file was not body-scanned.
    /// </summary>
    public bool? EntryHasLog { get; set; }
    public bool HasParseError { get; set; }
    public string? ParseError { get; set; }
}
