using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using UiPath.Engineering.Mcp.Core.CodeAnalysis;
using UiPath.Engineering.Mcp.Core.Models;

namespace UiPath.Engineering.Mcp.Core.Parsing;

/// <summary>
/// Finds coded-workflow invoke sites (<c>RunWorkflow</c> / <c>RunWorkflowAsync</c>
/// and generated <c>workflows.X(...)</c> helpers) via a Roslyn syntax walk, with an
/// optional semantic-model pass when analysis mode is not <see cref="CSharpAnalysisMode.SyntaxOnly"/>.
/// </summary>
public static class CodedWorkflowInvokeScanner {
    public static IReadOnlyList<InvokeWorkflowModel> Scan(
        string sourceIdentity,
        string sourceText,
        SemanticModel? semanticModel = null,
        CSharpAnalysisMode analysisMode = CSharpAnalysisMode.SyntaxOnly) {
        if (string.IsNullOrWhiteSpace(sourceText)) {
            return [];
        }

        var tree = semanticModel?.SyntaxTree
            ?? CSharpSyntaxTree.ParseText(sourceText);
        return Scan(sourceIdentity, tree, semanticModel, analysisMode);
    }

    public static IReadOnlyList<InvokeWorkflowModel> Scan(
        string sourceIdentity,
        SyntaxTree tree,
        SemanticModel? semanticModel = null,
        CSharpAnalysisMode analysisMode = CSharpAnalysisMode.SyntaxOnly) {
        var root = tree.GetRoot();
        var useSemantics = analysisMode != CSharpAnalysisMode.SyntaxOnly && semanticModel is not null;
        var invokes = new List<InvokeWorkflowModel>();

        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>()) {
            if (TryMatchRunWorkflow(invocation, useSemantics, semanticModel, out var runTarget, out var runDisplay)) {
                invokes.Add(new InvokeWorkflowModel {
                    SourceWorkflow = sourceIdentity,
                    TargetWorkflow = runTarget,
                    DisplayName = runDisplay,
                    ArgumentMappings = ExtractRunWorkflowMappings(invocation)
                });
                continue;
            }

            if (TryMatchWorkflowsHelper(invocation, useSemantics, semanticModel, out var helperTarget, out var helperDisplay)) {
                invokes.Add(new InvokeWorkflowModel {
                    SourceWorkflow = sourceIdentity,
                    TargetWorkflow = helperTarget,
                    DisplayName = helperDisplay,
                    ArgumentMappings = ExtractHelperMappings(invocation, useSemantics ? semanticModel : null)
                });
            }
        }

        return invokes;
    }

    /// <summary>
    /// Scans every syntax tree in <paramref name="context"/> whose path matches a
    /// coded workflow node, attaching invoke edges onto those <see cref="WorkflowModel"/>s.
    /// Syntax-only mode walks names without binding; full/partial uses the semantic model.
    /// </summary>
    public static void AttachInvokes(
        IReadOnlyList<WorkflowModel> codedWorkflowNodes,
        CSharpAnalysisContext? context) {
        if (codedWorkflowNodes.Count == 0) {
            return;
        }

        var byPath = new Dictionary<string, WorkflowModel>(StringComparer.OrdinalIgnoreCase);
        var byFileName = new Dictionary<string, WorkflowModel>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in codedWorkflowNodes) {
            if (!string.IsNullOrWhiteSpace(node.FilePath)) {
                byPath.TryAdd(NormalizePath(node.FilePath), node);
            }

            var identity = WorkflowPath.Identity(node);
            if (!string.IsNullOrWhiteSpace(identity)) {
                byPath.TryAdd(NormalizePath(identity), node);
            }

            byFileName.TryAdd(node.FileName, node);
        }

        if (context is null) {
            return;
        }

        foreach (var tree in context.Compilation.SyntaxTrees) {
            var path = tree.FilePath ?? string.Empty;
            if (!TryFindNode(path, byPath, byFileName, out var node)) {
                continue;
            }

            SemanticModel? model = null;
            if (context.Mode != CSharpAnalysisMode.SyntaxOnly) {
                model = context.Compilation.GetSemanticModel(tree);
            }

            var identity = WorkflowPath.Identity(node);
            // Replace prior coded edges for this node so a re-scan does not duplicate.
            node.InvokeWorkflows.RemoveAll(IsCodedInvoke);
            foreach (var invoke in Scan(identity, tree, model, context.Mode)) {
                node.InvokeWorkflows.Add(invoke);
            }
        }
    }

    private static bool IsCodedInvoke(InvokeWorkflowModel invoke) =>
        invoke.DisplayName is "RunWorkflow" or "RunWorkflowAsync"
        || invoke.DisplayName.StartsWith("workflows.", StringComparison.Ordinal);

    private static bool TryFindNode(
        string treePath,
        Dictionary<string, WorkflowModel> byPath,
        Dictionary<string, WorkflowModel> byFileName,
        out WorkflowModel node) {
        var normalized = NormalizePath(treePath);
        if (byPath.TryGetValue(normalized, out var exact)) {
            node = exact;
            return true;
        }

        // Match on trailing relative segment (compilation trees often carry absolute paths).
        foreach (var (key, candidate) in byPath) {
            if (normalized.EndsWith("/" + key, StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("\\" + key.Replace('/', '\\'), StringComparison.OrdinalIgnoreCase)) {
                node = candidate;
                return true;
            }
        }

        var fileName = Path.GetFileName(normalized);
        if (byFileName.TryGetValue(fileName, out var byName)) {
            node = byName;
            return true;
        }

        node = null!;
        return false;
    }

    private static string NormalizePath(string path) =>
        path.Replace('\\', '/');

    private static bool TryMatchRunWorkflow(
        InvocationExpressionSyntax invocation,
        bool useSemantics,
        SemanticModel? model,
        out string target,
        out string displayName) {
        target = string.Empty;
        displayName = string.Empty;

        var name = GetInvokedName(invocation);
        if (name is not ("RunWorkflow" or "RunWorkflowAsync")) {
            return false;
        }

        if (useSemantics && model is not null) {
            var info = model.GetSymbolInfo(invocation);
            var symbol = info.Symbol as IMethodSymbol
                ?? info.CandidateSymbols.OfType<IMethodSymbol>().FirstOrDefault();
            if (symbol is not null && !string.Equals(symbol.Name, name, StringComparison.Ordinal)) {
                return false;
            }
        }

        if (invocation.ArgumentList.Arguments.Count == 0) {
            return false;
        }

        var first = invocation.ArgumentList.Arguments[0].Expression;
        if (!TryExtractStringLiteral(first, out var path) || string.IsNullOrWhiteSpace(path)) {
            return false;
        }

        target = path;
        displayName = name;
        return true;
    }

    private static bool TryMatchWorkflowsHelper(
        InvocationExpressionSyntax invocation,
        bool useSemantics,
        SemanticModel? model,
        out string target,
        out string displayName) {
        target = string.Empty;
        displayName = string.Empty;

        if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess
            || !IsWorkflowsReceiver(memberAccess.Expression)) {
            return false;
        }

        var methodName = memberAccess.Name.Identifier.ValueText;
        if (string.IsNullOrWhiteSpace(methodName)) {
            return false;
        }

        if (useSemantics && model is not null) {
            var receiverNode = memberAccess.Expression switch {
                IdentifierNameSyntax id => (ExpressionSyntax)id,
                MemberAccessExpressionSyntax { Name: IdentifierNameSyntax name } => name,
                _ => memberAccess.Expression
            };
            var receiver = model.GetSymbolInfo(receiverNode).Symbol
                ?? model.GetSymbolInfo(receiverNode).CandidateSymbols.FirstOrDefault();
            // Prefer property/field named workflows; if unbound (missing references), keep the syntax hit.
            if (receiver is not null
                && receiver.Kind is not (SymbolKind.Property or SymbolKind.Field or SymbolKind.Parameter)
                && !string.Equals(receiver.Name, "workflows", StringComparison.Ordinal)) {
                return false;
            }
        }

        // Generated helpers are named after the workflow; prefer .cs, leave resolution to the graph.
        target = methodName.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
            || methodName.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)
            ? methodName
            : methodName + ".cs";
        displayName = $"workflows.{methodName}";
        return true;
    }

    private static bool IsWorkflowsReceiver(ExpressionSyntax expression) =>
        expression is IdentifierNameSyntax { Identifier.ValueText: "workflows" }
        || expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "workflows" };

    private static string? GetInvokedName(InvocationExpressionSyntax invocation) =>
        invocation.Expression switch {
            IdentifierNameSyntax id => id.Identifier.ValueText,
            MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
            _ => null
        };

    private static bool TryExtractStringLiteral(ExpressionSyntax expression, out string value) {
        switch (expression) {
            case LiteralExpressionSyntax { RawKind: (int)SyntaxKind.StringLiteralExpression } lit:
                value = lit.Token.ValueText;
                return true;
            case InterpolatedStringExpressionSyntax { Contents.Count: 1 } interp
                when interp.Contents[0] is InterpolatedStringTextSyntax text:
                value = text.TextToken.ValueText;
                return true;
            default:
                value = string.Empty;
                return false;
        }
    }

    private static List<ArgumentMappingModel> ExtractRunWorkflowMappings(InvocationExpressionSyntax invocation) {
        var mappings = new List<ArgumentMappingModel>();
        if (invocation.ArgumentList.Arguments.Count < 2) {
            return mappings;
        }

        var argsExpr = invocation.ArgumentList.Arguments[1].Expression;
        CollectDictionaryMappings(argsExpr, mappings);
        return mappings;
    }

    private static void CollectDictionaryMappings(ExpressionSyntax expression, List<ArgumentMappingModel> mappings) {
        // new Dictionary<...> { { "key", value }, ... } or new() { { "key", value } } / { ["key"] = value }
        var initializer = expression switch {
            ObjectCreationExpressionSyntax { Initializer: { } init } => init,
            ImplicitObjectCreationExpressionSyntax { Initializer: { } init } => init,
            _ => null
        };
        if (initializer is null) {
            return;
        }

        foreach (var expr in initializer.Expressions) {
            if (expr is InitializerExpressionSyntax nested
                && nested.Expressions.Count >= 2
                && TryExtractStringLiteral(nested.Expressions[0], out var key)) {
                mappings.Add(new ArgumentMappingModel {
                    Direction = "In",
                    TargetArgument = key,
                    Expression = nested.Expressions[1].ToString()
                });
            } else if (expr is AssignmentExpressionSyntax {
                Left: ImplicitElementAccessSyntax { ArgumentList.Arguments.Count: 1 } indexArgs,
                Right: var right
            } && TryExtractStringLiteral(indexArgs.ArgumentList.Arguments[0].Expression, out var indexKey)) {
                mappings.Add(new ArgumentMappingModel {
                    Direction = "In",
                    TargetArgument = indexKey,
                    Expression = right.ToString()
                });
            }
        }
    }

    private static List<ArgumentMappingModel> ExtractHelperMappings(
        InvocationExpressionSyntax invocation,
        SemanticModel? model) {
        var mappings = new List<ArgumentMappingModel>();
        IMethodSymbol? method = null;
        if (model is not null) {
            method = model.GetSymbolInfo(invocation).Symbol as IMethodSymbol
                ?? model.GetSymbolInfo(invocation).CandidateSymbols.OfType<IMethodSymbol>().FirstOrDefault();
        }

        var args = invocation.ArgumentList.Arguments;
        for (var i = 0; i < args.Count; i++) {
            var arg = args[i];
            string? targetName = null;
            if (arg.NameColon is { } named) {
                targetName = named.Name.Identifier.ValueText;
            } else if (method is not null && i < method.Parameters.Length) {
                targetName = method.Parameters[i].Name;
            }

            if (string.IsNullOrWhiteSpace(targetName)) {
                // Positional without a bound parameter name — do not invent a mapping.
                continue;
            }

            mappings.Add(new ArgumentMappingModel {
                Direction = "In",
                TargetArgument = targetName,
                Expression = arg.Expression.ToString()
            });
        }

        return mappings;
    }
}
