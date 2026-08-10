using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace UiPath.Engineering.Mcp.Core.CodeAnalysis;

/// <summary>
/// Semantic C# queries over the cached per-project <see cref="CSharpAnalysisContext"/>.
/// Stateless beyond the context cache; every method takes the project path.
/// </summary>
public sealed class CSharpAnalysisService : ICSharpAnalysisService {
    internal const int MaxResults = 200;
    internal const int MaxListItems = 25;
    internal const int MaxSourceLines = 200;

    private readonly ICSharpContextBuilder _contextBuilder;

    public CSharpAnalysisService(ICSharpContextBuilder contextBuilder) => _contextBuilder = contextBuilder;

    public async Task<FindSymbolResult> FindSymbolAsync(string projectPath, string symbol, string? kind = null, CancellationToken cancellationToken = default) {
        var context = await _contextBuilder.BuildAsync(projectPath, cancellationToken);

        var matches = context.Compilation
            .GetSymbolsWithName(symbol, SymbolFilter.All, cancellationToken)
            .Where(s => string.Equals(s.Name, symbol, StringComparison.Ordinal))
            .Where(s => s.Locations.Any(l => l.IsInSource))
            .Where(s => KindMatches(s, kind))
            .Take(MaxResults)
            .Select(ToSymbolMatch)
            .ToList();

        var result = new FindSymbolResult { Matches = matches };
        ApplyContext(result, context);
        return result;
    }

    public Task<FindReferencesResult> FindReferencesAsync(string projectPath, string symbol, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException(); // Task 6

    public Task<CodeContextResult> GetCodeContextAsync(string projectPath, string? symbol = null, string? file = null, int? line = null, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException(); // Task 7

    public Task<CompileDiagnosticsResult> GetDiagnosticsAsync(string projectPath, string? severity = null, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException(); // Task 8

    // --- shared helpers (also used by Tasks 6-8) -------------------------------

    internal static void ApplyContext(CSharpAnalysisResult result, CSharpAnalysisContext context) {
        result.AnalysisMode = context.Mode switch {
            CSharpAnalysisMode.Full => "full",
            CSharpAnalysisMode.Partial => "partial",
            _ => "syntaxOnly"
        };
        result.UnresolvedReferences = [.. context.UnresolvedReferences];
        result.Warnings = [.. context.Warnings];
        result.HasCSharpFiles = context.HasCSharpFiles;
        if (!context.HasCSharpFiles) {
            result.Note = "The project contains no C# files.";
        }
    }

    internal static bool KindMatches(ISymbol symbol, string? kind) => kind?.ToLowerInvariant() switch {
        null or "" => true,
        "method" => symbol.Kind == SymbolKind.Method,
        "property" => symbol.Kind == SymbolKind.Property,
        "field" => symbol.Kind == SymbolKind.Field,
        "class" => symbol is INamedTypeSymbol { TypeKind: TypeKind.Class },
        "interface" => symbol is INamedTypeSymbol { TypeKind: TypeKind.Interface },
        _ => true
    };

    internal static SymbolMatch ToSymbolMatch(ISymbol symbol) {
        var span = symbol.Locations.FirstOrDefault(l => l.IsInSource)?.GetLineSpan();
        return new SymbolMatch {
            Name = symbol.Name,
            Kind = symbol switch {
                INamedTypeSymbol type => type.TypeKind.ToString().ToLowerInvariant(),
                _ => symbol.Kind.ToString().ToLowerInvariant()
            },
            FilePath = span?.Path,
            Line = span is { } lineSpan ? lineSpan.StartLinePosition.Line + 1 : 0,
            ContainingType = symbol.ContainingType?.ToDisplayString(),
            Signature = symbol.ToDisplayString()
        };
    }
}
