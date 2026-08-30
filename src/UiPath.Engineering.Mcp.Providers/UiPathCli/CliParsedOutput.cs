using UiPath.Engineering.Mcp.Core.Parsing;

namespace UiPath.Engineering.Mcp.Providers.UiPathCli;

public sealed class CliParsedOutput {
    public List<string> Errors { get; } = [];
    public List<string> Warnings { get; } = [];
    public List<CliDiagnostic> Diagnostics { get; } = [];

    public void Deconstruct(out List<string> errors, out List<string> warnings) {
        errors = Errors;
        warnings = Warnings;
    }
}
