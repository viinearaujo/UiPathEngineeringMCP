namespace UiPath.Engineering.Mcp.Core.Parsing;

/// <summary>
/// One error or warning extracted from <c>uip rpa validate</c> / <c>build</c> JSON
/// (or a compiler-style <c>file.xaml(line): error CODE: message</c> line) before it is
/// mapped onto a snapshot activity ID.
/// </summary>
public sealed class CliDiagnostic {
    public string Message { get; init; } = string.Empty;
    public string? FilePath { get; init; }
    public int? Line { get; init; }
    public string? IdRef { get; init; }
    public string? DisplayName { get; init; }
    public string? Property { get; init; }
    public string? Recommendation { get; init; }
    public string? Code { get; init; }
    public string Severity { get; init; } = "error";
}
