using Microsoft.CodeAnalysis.CSharp;

namespace UiPath.Engineering.Mcp.Core.CodeAnalysis;

public enum CSharpAnalysisMode { Full, Partial, SyntaxOnly }

/// <summary>
/// A fully-built Roslyn compilation for one UiPath project, plus resolution
/// bookkeeping that tells callers how much they can trust semantic results.
/// Instances are immutable and safe to share across concurrent tool calls.
/// </summary>
public sealed class CSharpAnalysisContext {
    public required CSharpCompilation Compilation { get; init; }
    public required CSharpAnalysisMode Mode { get; init; }
    public IReadOnlyList<string> UnresolvedReferences { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public bool HasCSharpFiles { get; init; }
}
