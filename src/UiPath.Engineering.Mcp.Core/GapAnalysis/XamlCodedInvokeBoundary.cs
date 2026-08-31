using UiPath.Engineering.Mcp.Core.Models;

namespace UiPath.Engineering.Mcp.Core.GapAnalysis;

/// <summary>
/// Deterministic XAML ↔ coded boundary: XAML may invoke a coded workflow with
/// BCL / framework types, and must never pass types defined in this automation
/// or call coded-source methods.
/// </summary>
public static class XamlCodedInvokeBoundary {
    public const string NonPrimitiveIdPrefix = "coded-invoke-non-primitive";
    public const string SourceInvokeIdPrefix = "xaml-invokes-coded-source";
    public const string SourceMethodIdPrefix = "xaml-calls-coded-source-method";

    public static List<Gap> Lint(UiPathProjectModel model) {
        var gaps = new List<Gap>();
        var projectTypes = ProjectDefinedTypes.Collect(model.CodedWorkflows);
        var codedByFile = new Dictionary<string, CodedWorkflowModel>(StringComparer.OrdinalIgnoreCase);
        foreach (var coded in model.CodedWorkflows) {
            codedByFile.TryAdd(coded.FileName, coded);
        }

        foreach (var workflow in model.Workflows) {
            foreach (var invoke in workflow.InvokeWorkflows) {
                foreach (var mapping in invoke.ArgumentMappings) {
                    AddSourceMethodGaps(workflow.FileName, mapping.Expression, model.CodedWorkflows, gaps);
                }

                var targetName = Path.GetFileName(invoke.TargetWorkflow.Trim().Trim('"'));
                if (!targetName.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) {
                    continue;
                }

                if (!codedByFile.TryGetValue(targetName, out var target)) {
                    continue;
                }

                if (EffectiveKind(target) == CodedFileKind.Source) {
                    gaps.Add(new Gap {
                        Id = $"{SourceInvokeIdPrefix}:{workflow.FileName}->{targetName}",
                        Severity = Gap.Error,
                        Category = "boundary",
                        Message = $"'{workflow.FileName}' invokes coded source file '{targetName}'. XAML must never call methods on coded source types.",
                        TargetFile = workflow.FileName,
                        SuggestedTool = "add_coded_workflow",
                        SuggestedAction = "Do not invoke coded source files or their methods from XAML. Call a coded workflow that uses those helpers in C#."
                    });
                    continue;
                }

                CheckArgumentTypes(workflow.FileName, targetName, invoke, target, projectTypes, gaps);
            }

            foreach (var log in workflow.LogMessages) {
                AddSourceMethodGaps(workflow.FileName, log.Message, model.CodedWorkflows, gaps);
            }
        }

        return gaps;
    }

    public static bool IsAllowedArgumentType(string? type) =>
        IsAllowedArgumentType(type, codedFiles: null);

    public static bool IsAllowedArgumentType(string? type, IEnumerable<CodedWorkflowModel>? codedFiles) =>
        !ContainsProjectDefinedType(type, ProjectDefinedTypes.Collect(codedFiles));

    public static string EffectiveKind(CodedWorkflowModel coded) =>
        string.IsNullOrWhiteSpace(coded.Kind)
            ? (coded.IsCodedWorkflow ? CodedFileKind.Workflow : CodedFileKind.Source)
            : coded.Kind;

    private static void CheckArgumentTypes(
        string sourceFile,
        string targetName,
        InvokeWorkflowModel invoke,
        CodedWorkflowModel target,
        ProjectDefinedTypes projectTypes,
        List<Gap> gaps) {
        var entryByName = target.EntryArguments
            .GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Type, StringComparer.OrdinalIgnoreCase);

        foreach (var mapping in invoke.ArgumentMappings) {
            var type = mapping.Type;
            if (string.IsNullOrWhiteSpace(type)
                && entryByName.TryGetValue(mapping.TargetArgument, out var entryType)) {
                type = entryType;
            }

            if (IsAllowedArgumentType(type, projectTypes)) {
                continue;
            }

            gaps.Add(NonPrimitiveGap(sourceFile, targetName, mapping.TargetArgument, type));
        }

        foreach (var argument in target.EntryArguments) {
            if (invoke.ArgumentMappings.Any(m =>
                string.Equals(m.TargetArgument, argument.Name, StringComparison.OrdinalIgnoreCase))) {
                continue;
            }

            if (IsAllowedArgumentType(argument.Type, projectTypes)) {
                continue;
            }

            gaps.Add(NonPrimitiveGap(sourceFile, targetName, argument.Name, argument.Type));
        }
    }

    private static bool IsAllowedArgumentType(string? type, ProjectDefinedTypes projectTypes) =>
        !ContainsProjectDefinedType(type, projectTypes);

    private static Gap NonPrimitiveGap(string sourceFile, string targetName, string argument, string type) => new() {
        Id = $"{NonPrimitiveIdPrefix}:{sourceFile}->{targetName}:{argument}",
        Severity = Gap.Error,
        Category = "boundary",
        Message = $"'{sourceFile}' invokes coded workflow '{targetName}' with project-defined argument '{argument}' of type '{type}'.",
        TargetFile = sourceFile,
        SuggestedTool = "edit_workflow_file",
        SuggestedAction = "Pass BCL and framework types (including Dictionary, IEnumerable, DataTable, and arrays) across InvokeWorkflowFile into a coded workflow. Keep types defined in this automation inside .cs."
    };

    private static void AddSourceMethodGaps(
        string sourceFile,
        string? text,
        IEnumerable<CodedWorkflowModel> codedFiles,
        List<Gap> gaps) {
        if (string.IsNullOrWhiteSpace(text)) {
            return;
        }

        foreach (var coded in codedFiles) {
            if (EffectiveKind(coded) != CodedFileKind.Source || string.IsNullOrWhiteSpace(coded.ClassName)) {
                continue;
            }

            foreach (var method in coded.PublicMethods) {
                var token = coded.ClassName + "." + method;
                if (!text.Contains(token, StringComparison.Ordinal)) {
                    continue;
                }

                var id = $"{SourceMethodIdPrefix}:{sourceFile}:{coded.ClassName}.{method}";
                if (gaps.Any(g => g.Id == id)) {
                    continue;
                }

                gaps.Add(new Gap {
                    Id = id,
                    Severity = Gap.Error,
                    Category = "boundary",
                    Message = $"'{sourceFile}' calls coded source method '{token}'. XAML must never call methods on coded source types.",
                    TargetFile = sourceFile,
                    SuggestedTool = "add_coded_workflow",
                    SuggestedAction = "Do not invoke coded source files or their methods from XAML. Call a coded workflow that uses those helpers in C#."
                });
            }
        }
    }

    private static bool ContainsProjectDefinedType(string? type, ProjectDefinedTypes projectTypes) {
        if (string.IsNullOrWhiteSpace(type)) {
            return false;
        }

        var t = StripNullability(type.Trim());
        if (t.Length == 0) {
            return false;
        }

        if (TryUnwrapArray(t, out var elementType)) {
            return ContainsProjectDefinedType(elementType, projectTypes);
        }

        if (TrySplitGeneric(t, out var constructed, out var typeArguments)) {
            if (IsProjectDefinedLeaf(constructed, projectTypes)) {
                return true;
            }

            foreach (var argument in typeArguments) {
                if (ContainsProjectDefinedType(argument, projectTypes)) {
                    return true;
                }
            }

            return false;
        }

        return IsProjectDefinedLeaf(t, projectTypes);
    }

    private static string StripNullability(string type) {
        var t = type.Trim();
        while (t.EndsWith('?')) {
            t = t[..^1].Trim();
        }

        return t;
    }

    private static bool TryUnwrapArray(string type, out string elementType) {
        if (type.EndsWith("[]", StringComparison.Ordinal)) {
            elementType = type[..^2].Trim();
            return true;
        }

        if (type.EndsWith("()", StringComparison.Ordinal)) {
            elementType = type[..^2].Trim();
            return true;
        }

        if (type.EndsWith(']')) {
            var open = type.LastIndexOf('[');
            if (open >= 0 && IsArrayRankSpecifier(type.AsSpan(open))) {
                elementType = type[..open].Trim();
                return true;
            }
        }

        elementType = type;
        return false;
    }

    private static bool IsArrayRankSpecifier(ReadOnlySpan<char> specifier) {
        if (specifier.Length < 2 || specifier[0] != '[' || specifier[^1] != ']') {
            return false;
        }

        for (var i = 1; i < specifier.Length - 1; i++) {
            if (specifier[i] is not (',' or ' ')) {
                return false;
            }
        }

        return true;
    }

    private static bool TrySplitGeneric(string type, out string constructed, out List<string> typeArguments) {
        var vbOf = IndexOfVbOf(type);
        if (vbOf >= 0 && type.EndsWith(')')) {
            constructed = type[..vbOf].Trim();
            var innerStart = vbOf + 1;
            while (innerStart < type.Length && char.IsWhiteSpace(type[innerStart])) {
                innerStart++;
            }

            innerStart += 2;
            typeArguments = SplitTypeArguments(type[innerStart..^1]);
            return constructed.Length > 0 && typeArguments.Count > 0;
        }

        if (TryTakeBalanced(type, '<', '>', out constructed, out var inner)) {
            typeArguments = SplitTypeArguments(inner);
            return typeArguments.Count > 0;
        }

        if (TryTakeBalanced(type, '(', ')', out constructed, out inner) && inner.Length > 0) {
            typeArguments = SplitTypeArguments(inner);
            return typeArguments.Count > 0;
        }

        constructed = type;
        typeArguments = [];
        return false;
    }

    private static int IndexOfVbOf(string type) {
        for (var i = 0; i < type.Length; i++) {
            if (type[i] != '(') {
                continue;
            }

            var j = i + 1;
            while (j < type.Length && char.IsWhiteSpace(type[j])) {
                j++;
            }

            if (j + 1 < type.Length
                && type.AsSpan(j).StartsWith("Of", StringComparison.OrdinalIgnoreCase)) {
                var k = j + 2;
                if (k < type.Length && (char.IsWhiteSpace(type[k]) || type[k] is '<' or '(')) {
                    return i;
                }
            }
        }

        return -1;
    }

    private static bool TryTakeBalanced(
        string type,
        char open,
        char close,
        out string constructed,
        out string inner) {
        var start = type.IndexOf(open);
        if (start <= 0 || type[^1] != close) {
            constructed = type;
            inner = string.Empty;
            return false;
        }

        var depth = 0;
        for (var i = start; i < type.Length; i++) {
            var c = type[i];
            if (c == open) {
                depth++;
            } else if (c == close) {
                depth--;
                if (depth != 0) {
                    continue;
                }

                if (i != type.Length - 1) {
                    constructed = type;
                    inner = string.Empty;
                    return false;
                }

                constructed = type[..start].Trim();
                inner = type[(start + 1)..i];
                return constructed.Length > 0;
            }
        }

        constructed = type;
        inner = string.Empty;
        return false;
    }

    private static List<string> SplitTypeArguments(string raw) {
        var args = new List<string>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i < raw.Length; i++) {
            var c = raw[i];
            if (c is '<' or '(') {
                depth++;
            } else if (c is '>' or ')') {
                depth = Math.Max(0, depth - 1);
            } else if (c == ',' && depth == 0) {
                var piece = raw[start..i].Trim();
                if (piece.Length > 0) {
                    args.Add(piece);
                }

                start = i + 1;
            }
        }

        var last = raw[start..].Trim();
        if (last.Length > 0) {
            args.Add(last);
        }

        return args;
    }

    private static bool IsProjectDefinedLeaf(string type, ProjectDefinedTypes projectTypes) {
        var t = type.Trim();
        if (t.Length == 0) {
            return false;
        }

        string? prefix = null;
        var colon = t.LastIndexOf(':');
        if (colon >= 0) {
            prefix = t[..colon].Trim();
            t = t[(colon + 1)..].Trim();
        }

        if (prefix is not null && prefix.Equals("local", StringComparison.OrdinalIgnoreCase)) {
            return true;
        }

        var simpleName = t;
        var ns = string.Empty;
        var dot = t.LastIndexOf('.');
        if (dot >= 0) {
            ns = t[..dot];
            simpleName = t[(dot + 1)..];
        }

        if (simpleName.Length > 0 && projectTypes.IsProjectClass(simpleName)) {
            return true;
        }

        return projectTypes.IsProjectNamespace(ns);
    }

    private static bool IsSystemNamespace(string ns) =>
        ns.Equals("System", StringComparison.OrdinalIgnoreCase)
        || ns.StartsWith("System.", StringComparison.OrdinalIgnoreCase);

    private sealed class ProjectDefinedTypes {
        private readonly HashSet<string> _classNames;
        private readonly HashSet<string> _namespaces;

        private ProjectDefinedTypes(HashSet<string> classNames, HashSet<string> namespaces) {
            _classNames = classNames;
            _namespaces = namespaces;
        }

        public static ProjectDefinedTypes Collect(IEnumerable<CodedWorkflowModel>? codedFiles) {
            var classNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var namespaces = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (codedFiles is null) {
                return new ProjectDefinedTypes(classNames, namespaces);
            }

            foreach (var coded in codedFiles) {
                if (!string.IsNullOrWhiteSpace(coded.ClassName)
                    && EffectiveKind(coded) == CodedFileKind.Source) {
                    classNames.Add(coded.ClassName.Trim());
                }

                var ns = coded.Namespace?.Trim() ?? string.Empty;
                if (ns.Length > 0 && !IsSystemNamespace(ns)) {
                    namespaces.Add(ns);
                }
            }

            return new ProjectDefinedTypes(classNames, namespaces);
        }

        public bool IsProjectClass(string simpleName) => _classNames.Contains(simpleName);

        public bool IsProjectNamespace(string ns) {
            if (string.IsNullOrEmpty(ns) || IsSystemNamespace(ns)) {
                return false;
            }

            if (_namespaces.Contains(ns)) {
                return true;
            }

            foreach (var projectNs in _namespaces) {
                if (ns.StartsWith(projectNs + ".", StringComparison.OrdinalIgnoreCase)) {
                    return true;
                }
            }

            return false;
        }
    }
}
