using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using UiPath.Engineering.Mcp.Core.Models;

namespace UiPath.Engineering.Mcp.Core.Parsing;

/// <summary>
/// Syntax-only extraction of a UiPath coded workflow / test / source .cs file:
/// namespace, class name + base types, kind, [Workflow]/[TestCase] entry methods
/// with parameter types, and public method names. Never throws on bad input;
/// unparseable files come back with <see cref="CodedWorkflowModel.HasParseError"/>.
/// </summary>
public sealed class CodedSourceFileParser {
    public CodedWorkflowModel Parse(string fileName, string filePath, string content) {
        var model = new CodedWorkflowModel { FileName = fileName, FilePath = filePath };
        if (string.IsNullOrWhiteSpace(content)) {
            model.HasParseError = true;
            model.ParseError = "C# parse failure: file is empty.";
            return model;
        }

        var tree = CSharpSyntaxTree.ParseText(content);
        var root = tree.GetCompilationUnitRoot();
        var walker = new CodedFileWalker();
        walker.Visit(root);

        if (walker.ClassName is null) {
            model.HasParseError = true;
            model.ParseError = "C# parse failure: no class declaration found.";
            return model;
        }

        model.Namespace = walker.Namespace ?? string.Empty;
        model.ClassName = walker.ClassName;
        model.IsCodedWorkflow = walker.IsCodedWorkflow;
        model.EntryMethods.AddRange(walker.EntryMethods);
        model.EntryArguments.AddRange(walker.EntryArguments);
        model.PublicMethods.AddRange(walker.PublicMethods);

        if (walker.HasTestCase) {
            model.Kind = CodedFileKind.Test;
        } else if (model.IsCodedWorkflow || walker.HasWorkflow) {
            model.Kind = CodedFileKind.Workflow;
        } else {
            model.Kind = CodedFileKind.Source;
        }

        if (model.Kind == CodedFileKind.Workflow) {
            model.EntryHasTryCatch = walker.EntryHasTryCatch;
            model.EntryHasLog = walker.EntryHasLog;
        }

        return model;
    }

    private sealed class CodedFileWalker : CSharpSyntaxWalker {
        private ClassDeclarationSyntax? _chosen;
        public string? Namespace { get; private set; }
        public string? ClassName { get; private set; }
        public bool IsCodedWorkflow { get; private set; }
        public bool HasWorkflow { get; private set; }
        public bool HasTestCase { get; private set; }
        public List<string> EntryMethods { get; } = [];
        public List<ArgumentModel> EntryArguments { get; } = [];
        public List<string> PublicMethods { get; } = [];
        public bool EntryHasTryCatch { get; private set; } = true;
        public bool EntryHasLog { get; private set; }

        public override void VisitNamespaceDeclaration(NamespaceDeclarationSyntax node) {
            Namespace ??= node.Name.ToString();
            base.VisitNamespaceDeclaration(node);
        }

        public override void VisitFileScopedNamespaceDeclaration(FileScopedNamespaceDeclarationSyntax node) {
            Namespace ??= node.Name.ToString();
            base.VisitFileScopedNamespaceDeclaration(node);
        }

        public override void VisitClassDeclaration(ClassDeclarationSyntax node) {
            if (_chosen is not null && !InheritsCodedWorkflow(node)) {
                return;
            }

            _chosen = node;
            ClassName = node.Identifier.ValueText;
            IsCodedWorkflow = InheritsCodedWorkflow(node);
            ScanMembers(node);
        }

        private void ScanMembers(ClassDeclarationSyntax node) {
            EntryMethods.Clear();
            EntryArguments.Clear();
            PublicMethods.Clear();
            HasWorkflow = false;
            HasTestCase = false;
            EntryHasTryCatch = true;
            EntryHasLog = false;
            var sawWorkflowEntry = false;

            foreach (var member in node.Members) {
                if (member is not MethodDeclarationSyntax method) {
                    continue;
                }

                var name = method.Identifier.ValueText;
                var attr = EntryAttribute(method);
                if (attr is not null) {
                    if (!EntryMethods.Contains(name)) {
                        EntryMethods.Add(name);
                    }

                    if (attr.Equals("TestCase", StringComparison.OrdinalIgnoreCase)) {
                        HasTestCase = true;
                    } else {
                        HasWorkflow = true;
                    }

                    if (EntryArguments.Count == 0) {
                        EntryArguments.AddRange(ParseParameters(method.ParameterList));
                    }

                    if (attr.Equals("Workflow", StringComparison.OrdinalIgnoreCase)) {
                        sawWorkflowEntry = true;
                        var body = method.Body?.ToString() ?? method.ExpressionBody?.ToString() ?? string.Empty;
                        if (!HasTryCatch(method)) {
                            EntryHasTryCatch = false;
                        }

                        if (HasLog(body)) {
                            EntryHasLog = true;
                        }
                    }
                }

                if (method.Modifiers.Any(SyntaxKind.PublicKeyword)
                    && method.Identifier.ValueText != ClassName
                    && !EntryMethods.Contains(name)
                    && !PublicMethods.Contains(name)) {
                    PublicMethods.Add(name);
                }
            }

            if (!sawWorkflowEntry) {
                EntryHasTryCatch = false;
                EntryHasLog = false;
            }
        }

        private static bool InheritsCodedWorkflow(ClassDeclarationSyntax node) {
            if (node.BaseList is null) {
                return false;
            }

            return node.BaseList.Types.Any(t => {
                var text = t.Type.ToString();
                var simple = text.Contains('.') ? text[(text.LastIndexOf('.') + 1)..] : text;
                return simple == "CodedWorkflow";
            });
        }

        private static string? EntryAttribute(MethodDeclarationSyntax method) {
            foreach (var list in method.AttributeLists) {
                foreach (var attr in list.Attributes) {
                    var name = attr.Name.ToString();
                    var simple = name.Contains('.') ? name[(name.LastIndexOf('.') + 1)..] : name;
                    if (simple.Equals("Workflow", StringComparison.OrdinalIgnoreCase)
                        || simple.Equals("WorkflowAttribute", StringComparison.OrdinalIgnoreCase)) {
                        return "Workflow";
                    }

                    if (simple.Equals("TestCase", StringComparison.OrdinalIgnoreCase)
                        || simple.Equals("TestCaseAttribute", StringComparison.OrdinalIgnoreCase)) {
                        return "TestCase";
                    }
                }
            }

            return null;
        }

        private static bool HasTryCatch(MethodDeclarationSyntax method) {
            if (method.Body is null) {
                return false;
            }

            return method.Body.DescendantNodes().OfType<TryStatementSyntax>()
                .Any(t => t.Catches.Count > 0);
        }

        private static bool HasLog(string text) =>
            text.Contains("Log(", StringComparison.Ordinal)
            || text.Contains("LogMessage", StringComparison.Ordinal)
            || text.Contains("log.", StringComparison.Ordinal);

        private static List<ArgumentModel> ParseParameters(ParameterListSyntax parameters) {
            var args = new List<ArgumentModel>();
            foreach (var parameter in parameters.Parameters) {
                if (parameter.Type is null || string.IsNullOrWhiteSpace(parameter.Identifier.ValueText)) {
                    continue;
                }

                args.Add(new ArgumentModel {
                    Name = parameter.Identifier.ValueText.TrimStart('@'),
                    Type = parameter.Type.ToString(),
                    Direction = "In",
                    HasDefault = parameter.Default is not null
                });
            }

            return args;
        }
    }
}
