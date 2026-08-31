using UiPath.Engineering.Mcp.Core.CodeAnalysis;

namespace UiPath.Engineering.Mcp.Core.CodeSearch;

/// <summary>
/// Base shape every codebase-search response carries: truncation signal,
/// human-readable caveats, and non-fatal warnings.
/// </summary>
public abstract class CodebaseSearchResult {
    public bool Truncated { get; set; }
    public bool Stale { get; set; }
    public string? Note { get; set; }
    public List<string> Warnings { get; set; } = [];
}

public sealed class TextMatch {
    public string FilePath { get; init; } = string.Empty;
    public int Line { get; init; }
    public string Snippet { get; init; } = string.Empty;
}

public sealed class TextSearchResult : CodebaseSearchResult {
    public List<TextMatch> Matches { get; init; } = [];
    public int FilesSearched { get; set; }
    public List<string> SkippedFiles { get; init; } = [];
}

public sealed class SymbolSearchResult : CodebaseSearchResult {
    public List<SymbolMatch> Matches { get; init; } = [];
    public string AnalysisMode { get; set; } = "full";
    public List<string> UnresolvedReferences { get; set; } = [];
    public bool HasCSharpFiles { get; set; } = true;
}

public sealed class ActivityMatch {
    public string Id { get; init; } = string.Empty;
    public string WorkflowFile { get; init; } = string.Empty;
    public string WorkflowPath { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string ActivityType { get; init; } = string.Empty;
    public int Depth { get; init; }
    public int Line { get; init; }
}

public sealed class ActivitySearchResult : CodebaseSearchResult {
    public List<ActivityMatch> Matches { get; init; } = [];
    public int WorkflowsSearched { get; set; }
}

public sealed class WorkflowMatch {
    public string FileName { get; init; } = string.Empty;
    public string FilePath { get; init; } = string.Empty;
    public bool IsMain { get; init; }
    public string? Description { get; init; }
    public string MatchedOn { get; init; } = string.Empty; // name | description | both
}

public sealed class WorkflowSearchResult : CodebaseSearchResult {
    public List<WorkflowMatch> Matches { get; init; } = [];
}
