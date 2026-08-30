namespace UiPath.Engineering.Mcp.Core.Models;

/// <summary>
/// Validate/build diagnostic mapped onto a snapshot activity ID and a spec patch
/// Copilot can apply. JSON shape: <c>{ activityId, property, message, specFix }</c>.
/// </summary>
public sealed class ValidateFixDiagnostic {
    public string? ActivityId { get; init; }
    public string? Property { get; init; }
    public string Message { get; init; } = string.Empty;
    public SpecFixSuggestion? SpecFix { get; init; }
}

/// <summary>
/// Suggested merge into the activity spec for the mapped <see cref="ValidateFixDiagnostic.ActivityId"/>.
/// </summary>
public sealed class SpecFixSuggestion {
    public string? WorkflowFile { get; init; }
    public Dictionary<string, string?>? Properties { get; init; }
    public string? Hint { get; init; }
}
