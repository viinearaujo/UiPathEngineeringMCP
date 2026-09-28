using UiPath.Engineering.Mcp.Core.Parsing;

namespace UiPath.Engineering.Mcp.Providers.UiPathCli;

public sealed class CliParsedOutput {
    public List<string> Errors { get; } = [];
    public List<string> Warnings { get; } = [];
    public List<CliDiagnostic> Diagnostics { get; } = [];

    /// <summary>True when stdout contained a CLI response envelope.</summary>
    public bool EnvelopeRecognized { get; set; }

    /// <summary>True when that envelope's verdict is success.</summary>
    public bool EnvelopeSucceeded { get; set; }

    public void Deconstruct(out List<string> errors, out List<string> warnings) {
        errors = Errors;
        warnings = Warnings;
    }
}
