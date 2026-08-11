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

    public async Task<CodeContextResult> GetCodeContextAsync(string projectPath, string? symbol = null, string? file = null, int? line = null, CancellationToken cancellationToken = default) {
        var context = await _contextBuilder.BuildAsync(projectPath, cancellationToken);
        var result = new CodeContextResult();
        ApplyContext(result, context);

        var located = await LocateMemberAsync(context, symbol, file, line, cancellationToken);
        if (located is null) {
            result.Found = false;
            result.Note = symbol is null && file is null
                ? "Provide either 'symbol' or 'file' + 'line'."
                : "No matching member found for the given symbol or location.";
            return result;
        }

        var (member, model) = located;
        var declared = model.GetDeclaredSymbol(member, cancellationToken);
        var span = member.GetLocation().GetLineSpan();

        result.Found = true;
        result.Name = member switch {
            MethodDeclarationSyntax method => method.Identifier.Text,
            ConstructorDeclarationSyntax constructor => constructor.Identifier.Text,
            BaseTypeDeclarationSyntax type => type.Identifier.Text,
            PropertyDeclarationSyntax property => property.Identifier.Text,
            _ => member.GetType().Name
        };
        result.Kind = declared is not null
            ? ToSymbolMatch(declared).Kind
            : member.Kind().ToString().Replace("Declaration", string.Empty).ToLowerInvariant();
        result.FilePath = span.Path;
        result.Line = span.StartLinePosition.Line + 1;
        result.ContainingType = declared?.ContainingType?.ToDisplayString();
        result.Signature = declared?.ToDisplayString();

        result.CalledMethods = member.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Select(invocation => model.GetSymbolInfo(invocation, cancellationToken).Symbol as IMethodSymbol)
            .Where(method => method is not null)
            .Select(method => $"{method!.ContainingType?.Name}.{method.Name}")
            .Distinct()
            .Take(MaxListItems)
            .ToList();

        result.ReferencedTypes = member.DescendantNodes().OfType<TypeSyntax>()
            .Select(typeNode => model.GetTypeInfo(typeNode, cancellationToken).Type)
            .Where(type => type is { SpecialType: SpecialType.None } and not IErrorTypeSymbol)
            .Select(type => type!.Name)
            .Where(name => !string.IsNullOrEmpty(name))
            .Distinct()
            .Take(MaxListItems)
            .ToList();

        var source = member.ToString();
        var sourceLines = source.Split('\n');
        result.Truncated = sourceLines.Length > MaxSourceLines;
        result.Source = result.Truncated
            ? string.Join('\n', sourceLines.Take(MaxSourceLines))
            : source;
        return result;
    }

    private static readonly HashSet<string> MissingReferenceCodes = new(StringComparer.Ordinal) {
        "CS0234", // type or namespace does not exist in namespace
        "CS0246", // type or namespace could not be found
        "CS0012"  // type is defined in an assembly that is not referenced
    };

    public async Task<CompileDiagnosticsResult> GetDiagnosticsAsync(string projectPath, string? severity = null, CancellationToken cancellationToken = default) {
        var context = await _contextBuilder.BuildAsync(projectPath, cancellationToken);
        var result = new CompileDiagnosticsResult();
        ApplyContext(result, context);

        if (context.Mode == CSharpAnalysisMode.SyntaxOnly) {
            result.Note = "References could not be resolved; compiler diagnostics are unavailable in syntaxOnly mode.";
            return result;
        }

        var minSeverity = severity?.ToLowerInvariant() switch {
            "all" => DiagnosticSeverity.Hidden,
            "warning" => DiagnosticSeverity.Warning,
            _ => DiagnosticSeverity.Error
        };

        foreach (var diagnostic in context.Compilation.GetDiagnostics(cancellationToken)) {
            if (result.Diagnostics.Count >= MaxResults) {
                result.Truncated = true;
                break;
            }
            if (diagnostic.Severity < minSeverity || !diagnostic.Location.IsInSource) {
                continue;
            }
            if (context.Mode == CSharpAnalysisMode.Partial && MissingReferenceCodes.Contains(diagnostic.Id)) {
                result.SuppressedMissingReferenceDiagnostics++;
                continue;
            }

            var span = diagnostic.Location.GetLineSpan();
            result.Diagnostics.Add(new DiagnosticItem {
                FilePath = span.Path ?? string.Empty,
                Line = span.StartLinePosition.Line + 1,
                Column = span.StartLinePosition.Character + 1,
                Code = diagnostic.Id,
                Severity = diagnostic.Severity.ToString().ToLowerInvariant(),
                Message = diagnostic.GetMessage()
            });
        }

        if (result.SuppressedMissingReferenceDiagnostics > 0) {
            result.Note = $"Suppressed {result.SuppressedMissingReferenceDiagnostics} diagnostics caused by unresolved references ({string.Join(", ", context.UnresolvedReferences)}). Resolve the packages and re-run for full diagnostics.";
        }
        if (result.Truncated) {
            var truncationNote = $"Results truncated at {MaxResults} diagnostics; narrow with the 'severity' parameter or fix the first errors and re-run.";
            result.Note = result.Note is null ? truncationNote : result.Note + " " + truncationNote;
        }
        return result;
    }

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

    private sealed record LocatedMember(MemberDeclarationSyntax Member, SemanticModel Model);

    private static async Task<LocatedMember?> LocateMemberAsync(
        CSharpAnalysisContext context, string? symbol, string? file, int? line, CancellationToken cancellationToken) {
        if (!string.IsNullOrWhiteSpace(symbol)) {
            var target = context.Compilation
                .GetSymbolsWithName(symbol, SymbolFilter.All, cancellationToken)
                .Where(s => string.Equals(s.Name, symbol, StringComparison.Ordinal))
                .Where(s => s.Locations.Any(l => l.IsInSource))
                .OrderByDescending(s => s.Kind == SymbolKind.Method) // prefer methods over types
                .FirstOrDefault();
            var reference = target?.DeclaringSyntaxReferences.FirstOrDefault();
            if (reference is null) {
                return null;
            }
            var node = reference.GetSyntax(cancellationToken);
            var member = node.AncestorsAndSelf().OfType<MemberDeclarationSyntax>().FirstOrDefault();
            return member is null
                ? null
                : new LocatedMember(member, context.Compilation.GetSemanticModel(member.SyntaxTree));
        }

        if (!string.IsNullOrWhiteSpace(file) && line is > 0) {
            var tree = context.Compilation.SyntaxTrees.FirstOrDefault(t =>
                string.Equals(t.FilePath, file, StringComparison.OrdinalIgnoreCase));
            if (tree is null) {
                return null;
            }
            var text = await tree.GetTextAsync(cancellationToken);
            if (line.Value > text.Lines.Count) {
                return null;
            }
            var position = text.Lines[line.Value - 1].Start;
            var root = await tree.GetRootAsync(cancellationToken);
            var token = root.FindToken(position);
            var member = token.Parent?.AncestorsAndSelf().OfType<MemberDeclarationSyntax>().FirstOrDefault();
            return member is null
                ? null
                : new LocatedMember(member, context.Compilation.GetSemanticModel(tree));
        }

        return null;
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
