using UiPath.Engineering.Mcp.Core;
using UiPath.Engineering.Mcp.Core.Parsing;

namespace UiPath.Engineering.Mcp.Providers.UiPathCli;

/// <summary>
/// Redacts secrets from CLI parser output before it leaves the process boundary.
/// </summary>
public static class CliOutputRedactor {
    internal static CliDiagnostic Redact(CliDiagnostic diagnostic) => new() {
        Message = Redact(diagnostic.Message),
        FilePath = diagnostic.FilePath,
        Line = diagnostic.Line,
        IdRef = diagnostic.IdRef,
        DisplayName = diagnostic.DisplayName,
        Property = diagnostic.Property,
        Recommendation = diagnostic.Recommendation is null ? null : Redact(diagnostic.Recommendation),
        Code = diagnostic.Code,
        Severity = diagnostic.Severity
    };

    internal static string Redact(string entry) => SecretRedactor.Redact(entry).Text;
}
