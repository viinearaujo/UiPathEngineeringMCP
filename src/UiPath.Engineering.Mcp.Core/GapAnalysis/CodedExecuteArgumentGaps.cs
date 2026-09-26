using UiPath.Engineering.Mcp.Core.Models;

namespace UiPath.Engineering.Mcp.Core.GapAnalysis;

/// <summary>
/// Compares a coded workflow's <c>Execute</c> parameters with the XAML
/// <c>InvokeWorkflowFile</c> argument bindings and reports missing, extra, or renamed names.
/// </summary>
public static class CodedExecuteArgumentGaps {
    public const string MissingIdPrefix = "coded-invoke-arg-missing";
    public const string ExtraIdPrefix = "coded-invoke-arg-extra";
    public const string RenamedIdPrefix = "coded-invoke-arg-renamed";

    public static List<Gap> Lint(UiPathProjectModel model) {
        var gaps = new List<Gap>();
        var codedByFile = new Dictionary<string, CodedWorkflowModel>(StringComparer.OrdinalIgnoreCase);
        foreach (var coded in model.CodedWorkflows) {
            codedByFile.TryAdd(coded.FileName, coded);
        }

        foreach (var workflow in model.Workflows) {
            foreach (var invoke in workflow.InvokeWorkflows) {
                var targetName = Path.GetFileName(invoke.TargetWorkflow.Trim().Trim('"'));
                if (!targetName.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                    || !codedByFile.TryGetValue(targetName, out var target)
                    || XamlCodedInvokeBoundary.EffectiveKind(target) != CodedFileKind.Workflow) {
                    continue;
                }

                Compare(workflow.FileName, targetName, invoke, target, gaps);
            }
        }

        return gaps;
    }

    private static void Compare(
        string sourceFile,
        string targetName,
        InvokeWorkflowModel invoke,
        CodedWorkflowModel target,
        List<Gap> gaps) {
        var execute = target.EntryArguments
            .Where(a => !string.IsNullOrWhiteSpace(a.Name))
            .GroupBy(a => a.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var bindings = invoke.ArgumentMappings
            .Where(m => !string.IsNullOrWhiteSpace(m.TargetArgument))
            .GroupBy(m => m.TargetArgument.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var unbound = new List<ArgumentModel>();
        foreach (var argument in execute.Values) {
            if (!bindings.ContainsKey(argument.Name)) {
                unbound.Add(argument);
            }
        }

        var extra = new List<ArgumentMappingModel>();
        foreach (var mapping in bindings.Values) {
            if (!execute.ContainsKey(mapping.TargetArgument)) {
                extra.Add(mapping);
            }
        }

        var pairedArguments = new HashSet<ArgumentModel>();
        var pairedBindings = new HashSet<ArgumentMappingModel>();
        foreach (var (argument, mapping) in PairRenames(unbound, extra)) {
            pairedArguments.Add(argument);
            pairedBindings.Add(mapping);
            gaps.Add(RenamedGap(sourceFile, targetName, mapping.TargetArgument.Trim(), argument.Name.Trim()));
        }

        foreach (var argument in unbound) {
            if (pairedArguments.Contains(argument) || argument.HasDefault) {
                continue;
            }

            gaps.Add(MissingGap(sourceFile, targetName, argument.Name.Trim()));
        }

        foreach (var mapping in extra) {
            if (pairedBindings.Contains(mapping)) {
                continue;
            }

            gaps.Add(ExtraGap(sourceFile, targetName, mapping.TargetArgument.Trim()));
        }
    }

    // Pair an unbound Execute parameter with an extra binding when the names share a core
    // (directional prefix ignored) or a long common prefix. A tie is left as missing/extra.
    private static List<(ArgumentModel Argument, ArgumentMappingModel Mapping)> PairRenames(
        List<ArgumentModel> unbound,
        List<ArgumentMappingModel> extra) {
        var candidates = new List<(ArgumentModel Argument, ArgumentMappingModel Mapping, int Score)>();
        foreach (var argument in unbound) {
            foreach (var mapping in extra) {
                var score = RenameScore(argument.Name, mapping.TargetArgument);
                if (score > 0) {
                    candidates.Add((argument, mapping, score));
                }
            }
        }

        var chosen = new List<(ArgumentModel Argument, ArgumentMappingModel Mapping)>();
        var usedArguments = new HashSet<ArgumentModel>();
        var usedBindings = new HashSet<ArgumentMappingModel>();
        foreach (var candidate in candidates
            .OrderByDescending(c => c.Score)
            .ThenBy(c => c.Argument.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.Mapping.TargetArgument, StringComparer.OrdinalIgnoreCase)) {
            if (usedArguments.Contains(candidate.Argument) || usedBindings.Contains(candidate.Mapping)) {
                continue;
            }

            var contested = candidates.Any(other =>
                other.Score >= candidate.Score
                && (ReferenceEquals(other.Argument, candidate.Argument) || ReferenceEquals(other.Mapping, candidate.Mapping))
                && !(ReferenceEquals(other.Argument, candidate.Argument) && ReferenceEquals(other.Mapping, candidate.Mapping)));
            if (contested) {
                continue;
            }

            usedArguments.Add(candidate.Argument);
            usedBindings.Add(candidate.Mapping);
            chosen.Add((candidate.Argument, candidate.Mapping));
        }

        return chosen;
    }

    private static int RenameScore(string executeName, string bindingName) {
        var execute = Core(executeName);
        var binding = Core(bindingName);
        if (execute.Length == 0 || binding.Length == 0) {
            return 0;
        }

        if (execute.Equals(binding, StringComparison.OrdinalIgnoreCase)) {
            return 1000;
        }

        var prefix = CommonPrefixLength(execute, binding);
        var shorter = Math.Min(execute.Length, binding.Length);
        return prefix >= 4 && prefix * 2 >= shorter ? prefix : 0;
    }

    private static string Core(string name) {
        var trimmed = name.Trim();
        if (trimmed.StartsWith("in_", StringComparison.OrdinalIgnoreCase)) {
            return trimmed[3..];
        }

        if (trimmed.StartsWith("out_", StringComparison.OrdinalIgnoreCase)) {
            return trimmed[4..];
        }

        if (trimmed.StartsWith("io_", StringComparison.OrdinalIgnoreCase)) {
            return trimmed[3..];
        }

        return trimmed;
    }

    private static int CommonPrefixLength(string left, string right) {
        var count = Math.Min(left.Length, right.Length);
        var index = 0;
        while (index < count && char.ToUpperInvariant(left[index]) == char.ToUpperInvariant(right[index])) {
            index++;
        }

        return index;
    }

    private static Gap MissingGap(string sourceFile, string targetName, string name) => new() {
        Id = $"{MissingIdPrefix}:{sourceFile}->{targetName}:{name}",
        Severity = Gap.Error,
        Category = "boundary",
        Message = $"'{sourceFile}' invokes coded workflow '{targetName}' missing Execute parameter '{name}'.",
        TargetFile = sourceFile,
        SuggestedTool = "insert_activities",
        SuggestedAction = $"Add argument '{name}' to the InvokeWorkflowFile that calls '{targetName}' with insert_activities."
    };

    private static Gap ExtraGap(string sourceFile, string targetName, string name) => new() {
        Id = $"{ExtraIdPrefix}:{sourceFile}->{targetName}:{name}",
        Severity = Gap.Error,
        Category = "boundary",
        Message = $"'{sourceFile}' invokes coded workflow '{targetName}' with extra argument '{name}'.",
        TargetFile = sourceFile,
        SuggestedTool = "insert_activities",
        SuggestedAction = $"Remove argument '{name}' from the InvokeWorkflowFile that calls '{targetName}' with insert_activities."
    };

    private static Gap RenamedGap(string sourceFile, string targetName, string bindingName, string executeName) => new() {
        Id = $"{RenamedIdPrefix}:{sourceFile}->{targetName}:{bindingName}->{executeName}",
        Severity = Gap.Error,
        Confidence = Gap.ConfidenceMedium,
        Category = "boundary",
        Message = $"'{sourceFile}' invokes coded workflow '{targetName}' with renamed argument '{bindingName}' (Execute declares '{executeName}').",
        TargetFile = sourceFile,
        SuggestedTool = "manage_workflow_data",
        SuggestedAction = $"Rename the InvokeWorkflowFile binding from '{bindingName}' to '{executeName}' with manage_workflow_data."
    };
}
