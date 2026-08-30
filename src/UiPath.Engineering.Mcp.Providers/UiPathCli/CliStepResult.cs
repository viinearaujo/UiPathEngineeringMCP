using UiPath.Engineering.Mcp.Core.Parsing;

namespace UiPath.Engineering.Mcp.Providers.UiPathCli;
public sealed class CliStepResult {
    /// <summary>False when the step was not requested or was skipped after an earlier step failed.</summary>
    public bool Executed { get; init; }
    public bool Success { get; init; }
    public List<string> Errors { get; init; } = [];
    public List<string> Warnings { get; init; } = [];
    public List<CliDiagnostic> Diagnostics { get; init; } = [];
}
