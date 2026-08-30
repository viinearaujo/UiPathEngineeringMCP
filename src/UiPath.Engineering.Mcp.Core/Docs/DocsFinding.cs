namespace UiPath.Engineering.Mcp.Core.Docs;

public sealed class DocsFinding {
    public const string Error = "error";
    public const string Warning = "warning";

    public string Code { get; init; } = string.Empty;
    public string Severity { get; init; } = Warning;
    public string Message { get; init; } = string.Empty;
    public string? TargetFile { get; init; }
    public string FixHint { get; init; } = string.Empty;
    public string? SuggestedTool { get; init; }
}
