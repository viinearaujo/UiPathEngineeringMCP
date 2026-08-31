namespace UiPath.Engineering.Mcp.Core.CodeAnalysis;

/// <summary>
/// Base shape every C# analysis tool response carries: how much the results can be
/// trusted ("full" | "partial" | "syntaxOnly") and what could not be resolved.
/// </summary>
public abstract class CSharpAnalysisResult {
    public string AnalysisMode { get; set; } = "full";
    public List<string> UnresolvedReferences { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
    public bool HasCSharpFiles { get; set; } = true;
    public bool Stale { get; set; }
    public string? Note { get; set; }
}

public sealed class SymbolMatch {
    public string Name { get; init; } = string.Empty;
    public string Kind { get; init; } = string.Empty;
    public string? FilePath { get; init; }
    public int Line { get; init; }
    public string? ContainingType { get; init; }
    public string Signature { get; init; } = string.Empty;
}

public sealed class FindSymbolResult : CSharpAnalysisResult {
    public List<SymbolMatch> Matches { get; init; } = [];
}

public sealed class ReferenceMatch {
    public string FilePath { get; init; } = string.Empty;
    public int Line { get; init; }
    public string? ContainingMember { get; init; }
    public string Snippet { get; init; } = string.Empty;
}

public sealed class FindReferencesResult : CSharpAnalysisResult {
    public List<ReferenceMatch> References { get; init; } = [];
}

public sealed class CodeContextResult : CSharpAnalysisResult {
    public bool Found { get; set; }
    public string? Name { get; set; }
    public string? Kind { get; set; }
    public string? FilePath { get; set; }
    public int Line { get; set; }
    public string? ContainingType { get; set; }
    public string? Signature { get; set; }
    public List<string> CalledMethods { get; set; } = [];
    public List<string> ReferencedTypes { get; set; } = [];
    public string? Source { get; set; }
    public bool Truncated { get; set; }
}

public sealed class DiagnosticItem {
    public string FilePath { get; init; } = string.Empty;
    public int Line { get; init; }
    public int Column { get; init; }
    public string Code { get; init; } = string.Empty;
    public string Severity { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
}

public sealed class CompileDiagnosticsResult : CSharpAnalysisResult {
    public List<DiagnosticItem> Diagnostics { get; init; } = [];
    public int SuppressedMissingReferenceDiagnostics { get; set; }

    // True when more diagnostics existed than were returned (list capped at MaxResults).
    public bool Truncated { get; set; }
}
