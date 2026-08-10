using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

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

    public async Task<FindReferencesResult> FindReferencesAsync(string projectPath, string symbol, CancellationToken cancellationToken = default) {
        var context = await _contextBuilder.BuildAsync(projectPath, cancellationToken);

        // Source-declared target symbols with this exact name (may be empty: the symbol
        // can live in referenced metadata or be unresolvable in degraded modes).
        var targets = context.Compilation
            .GetSymbolsWithName(symbol, SymbolFilter.All, cancellationToken)
            .Where(s => string.Equals(s.Name, symbol, StringComparison.Ordinal))
            .Where(s => s.Locations.Any(l => l.IsInSource))
            .ToList();

        var matches = new List<ReferenceMatch>();
        foreach (var tree in context.Compilation.SyntaxTrees) {
            cancellationToken.ThrowIfCancellationRequested();
            var model = context.Compilation.GetSemanticModel(tree);
            var root = await tree.GetRootAsync(cancellationToken);
            var text = await tree.GetTextAsync(cancellationToken);

            foreach (var node in root.DescendantNodes().OfType<SimpleNameSyntax>()) {
                if (matches.Count >= MaxResults) {
                    break;
                }
                if (!string.Equals(node.Identifier.Text, symbol, StringComparison.Ordinal)) {
                    continue;
                }

                // Declaration identifiers are tokens, not SimpleNameSyntax nodes, so
                // declarations never appear here; every candidate is a usage site.
                var info = model.GetSymbolInfo(node, cancellationToken);
                var candidate = info.Symbol ?? info.CandidateSymbols.FirstOrDefault();

                var isReference = targets.Count > 0
                    ? targets.Any(t => SymbolMatchesTarget(candidate, t))
                    : candidate is null; // fallback: only truly unresolved identifiers
                if (!isReference) {
                    continue;
                }

                matches.Add(ToReferenceMatch(text, node));
            }
        }

        var result = new FindReferencesResult { References = matches };
        ApplyContext(result, context);
        if (targets.Count == 0) {
            result.Note = $"'{symbol}' is not declared in this project's source; matches are identifier-based and may include false positives.";
        }
        return result;
    }

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

    private static bool SymbolMatchesTarget(ISymbol? candidate, ISymbol target) {
        if (candidate is null) {
            return false;
        }
        return SymbolEqualityComparer.Default.Equals(candidate, target)
            || SymbolEqualityComparer.Default.Equals(candidate.OriginalDefinition, target.OriginalDefinition);
    }

    private static ReferenceMatch ToReferenceMatch(SourceText text, SimpleNameSyntax node) {
        var span = node.GetLocation().GetLineSpan();
        var containing = node.Ancestors().OfType<MemberDeclarationSyntax>().FirstOrDefault();
        var containingName = containing switch {
            MethodDeclarationSyntax method => method.Identifier.Text,
            ConstructorDeclarationSyntax constructor => constructor.Identifier.Text,
            BaseTypeDeclarationSyntax type => type.Identifier.Text,
            PropertyDeclarationSyntax property => property.Identifier.Text,
            _ => null
        };
        var lineIndex = span.StartLinePosition.Line;
        return new ReferenceMatch {
            FilePath = span.Path ?? string.Empty,
            Line = lineIndex + 1,
            ContainingMember = containingName,
            Snippet = lineIndex < text.Lines.Count ? text.Lines[lineIndex].ToString().Trim() : string.Empty
        };
    }
}
