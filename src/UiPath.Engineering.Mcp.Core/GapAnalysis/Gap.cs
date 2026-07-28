namespace UiPath.Engineering.Mcp.Core.GapAnalysis;

public sealed class Gap {
    public const string Error = "error";
    public const string Warning = "warning";
    public const string Info = "info";

    public string Id { get; init; } = string.Empty;
    public string Severity { get; init; } = Info;
    public string Category { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public string? TargetFile { get; init; }
    public string? SuggestedTool { get; init; }
    public string? SuggestedAction { get; init; }
}
