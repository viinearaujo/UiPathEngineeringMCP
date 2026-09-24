namespace UiPath.Engineering.Mcp.Core.GapAnalysis;

/// <summary>
/// One deterministic hygiene finding. <see cref="Severity"/> says how much the finding
/// matters when it is real; <see cref="Confidence"/> says how much evidence backs it.
/// A low-confidence gap is a hint produced by substring or namespace-blind matching —
/// verify it before editing, and never treat it as a done gate.
/// </summary>
public sealed class Gap {
    public const string Error = "error";
    public const string Warning = "warning";
    public const string Info = "info";

    /// <summary>Structural evidence: parsed XAML members, the invoke graph, project.json.</summary>
    public const string ConfidenceHigh = "high";

    /// <summary>Structural detection of an opinionated convention, or name/slot inference.</summary>
    public const string ConfidenceMedium = "medium";

    /// <summary>Substring or namespace-blind heuristic. A hint to verify, not an asserted fact.</summary>
    public const string ConfidenceLow = "low";

    public const string CategoryReadability = "readability";

    /// <summary>
    /// Reporting order for <c>analyze_project_gaps</c>: blocking correctness first, then the
    /// boundary and resilience concerns the agent loop must close, then advisory hygiene.
    /// Categories not listed here sort last, alphabetically.
    /// </summary>
    public static readonly string[] CategoryOrder = [
        "project",
        "structure",
        "boundary",
        "resilience",
        "observability",
        CategoryReadability,
        "documentation",
        "testing",
        "plan",
        "docs"
    ];

    public static int CategoryRank(string? category) {
        var index = Array.FindIndex(CategoryOrder, c => string.Equals(c, category, StringComparison.Ordinal));
        return index < 0 ? CategoryOrder.Length : index;
    }

    public static int ConfidenceRank(string? confidence) => confidence switch {
        ConfidenceHigh => 3,
        ConfidenceMedium => 2,
        _ => 1
    };

    public string Id { get; init; } = string.Empty;
    public string Severity { get; init; } = Info;

    /// <summary>
    /// One of <see cref="ConfidenceHigh"/>, <see cref="ConfidenceMedium"/>, or
    /// <see cref="ConfidenceLow"/>. Defaults to high because most rules read parsed
    /// structure; heuristics set it explicitly.
    /// </summary>
    public string Confidence { get; init; } = ConfidenceHigh;

    public string Category { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public string? TargetFile { get; init; }
    public string? SuggestedTool { get; init; }
    public string? SuggestedAction { get; init; }
}
