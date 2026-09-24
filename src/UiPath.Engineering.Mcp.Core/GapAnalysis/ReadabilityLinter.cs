using System.Text;
using UiPath.Engineering.Mcp.Core.Models;
using UiPath.Engineering.Mcp.Core.Parsing;

namespace UiPath.Engineering.Mcp.Core.GapAnalysis;

/// <summary>
/// Readability and naming rules: DisplayName quality, the <c>in_</c>/<c>out_</c>/<c>io_</c>
/// argument convention, container nesting and activity counts, the Rule 24 body wrap,
/// workflow description coverage, and REFramework conformance.
/// </summary>
/// <remarks>
/// Every rule reads the parsed <see cref="UiPathProjectModel"/> only — no file I/O and no
/// type resolution. <see cref="XamlWorkflowParser"/> matches on XAML local names and is
/// namespace-blind, so <c>ui:Click</c>, <c>uix:NClick</c> and a mobile <c>Click</c> are
/// indistinguishable here. Rules keyed on an activity type name therefore carry low or
/// medium <see cref="Gap.Confidence"/> and are worded as hints to verify, never as facts.
/// </remarks>
internal static class ReadabilityLinter {
    /// <summary>Direct children of one container above which the body should be regrouped.</summary>
    internal const int MaxContainerActivityCount = 12;

    /// <summary>Activities above which a workflow should be split (project-structure-guide: ~20-30).</summary>
    internal const int MaxWorkflowActivityCount = 30;

    /// <summary>Nesting levels (root body = 1) above which control flow should be lifted out.</summary>
    internal const int MaxNestingLevels = 7;

    /// <summary>Parsed workflows below which project-wide advice is not worth reporting.</summary>
    internal const int MinWorkflowsForProjectAdvice = 3;

    private const int MaxReportedOffenders = 3;

    /// <summary>The four REFramework state files, in state order (reframework-guide § The Four States).</summary>
    internal static readonly string[] ReFrameworkFiles = [
        "InitAllSettings.xaml",
        "GetTransactionData.xaml",
        "Process.xaml",
        "SetTransactionStatus.xaml"
    ];

    /// <summary>
    /// Containers with exactly one body slot that holds a single <c>Activity</c>. Attached-property
    /// slots are transparent to <see cref="XamlActivityLocator"/>, so a direct child of one of these
    /// is body content; two or more cannot all be.
    /// </summary>
    private static readonly HashSet<string> SingleBodyContainers = new(StringComparer.Ordinal) {
        "While",
        "DoWhile",
        "InterruptibleWhile",
        "InterruptibleDoWhile",
        "RetryScope"
    };

    private static readonly string[] PlaceholderMarkers = [
        "TODO",
        "TBD",
        "XXX",
        "placeholder",
        "NewActivity",
        "Activity1"
    ];

    public static void Lint(UiPathProjectModel model, List<Gap> gaps) {
        var isLibrary = IsLibrary(model);
        foreach (var workflow in model.Workflows) {
            if (workflow.HasParseError) {
                continue;
            }

            // Argument naming reads the parsed x:Property members, so it applies to a
            // workflow that declares arguments but has no body yet.
            if (!isLibrary) {
                LintArgumentPrefixes(workflow.FileName, workflow.Arguments, gaps);
            }

            if (workflow.Activities.Count == 0) {
                continue;
            }

            LintDisplayNames(workflow, gaps);
            LintComplexity(workflow, gaps);
            LintContainerBodies(workflow, gaps);
        }

        LintDescriptionCoverage(model, gaps);
        LintReFramework(model, gaps);
    }

    // ---------------------------------------------------------------- DisplayName

    private static void LintDisplayNames(WorkflowModel workflow, List<Gap> gaps) {
        var offenders = new List<string>();
        foreach (var activity in workflow.Activities) {
            if (IsGenericDisplayName(activity.DisplayName, activity.Type)) {
                offenders.Add(Describe(activity));
            }
        }

        if (offenders.Count == 0) {
            return;
        }

        gaps.Add(new Gap {
            Id = $"generic-displayname:{workflow.FileName}",
            Severity = Gap.Info,
            // The type-name default is compared against a namespace-blind local name.
            Confidence = Gap.ConfidenceLow,
            Category = Gap.CategoryReadability,
            Message = $"'{workflow.FileName}' has {offenders.Count} activity(ies) with a missing, generic, or "
                + $"type-name-default DisplayName: {Summarize(offenders)}. Hint: verify against the file, then give "
                + "each activity a business-meaningful DisplayName.",
            TargetFile = workflow.FileName,
            SuggestedTool = "edit_workflow_file",
            SuggestedAction = "Replace each default DisplayName with a name that says what the step does in this process."
        });
    }

    private static bool IsGenericDisplayName(string displayName, string type) {
        if (string.IsNullOrWhiteSpace(displayName)) {
            return true;
        }

        var normalized = NormalizeForCompare(displayName);
        if (normalized.Length == 0) {
            return true;
        }

        if (PlaceholderMarkers.Any(marker => normalized.Contains(marker, StringComparison.OrdinalIgnoreCase))) {
            return true;
        }

        return MatchesTypeDefault(normalized, type);
    }

    // Studio leaves DisplayName at the type name, its spaced form ("Read Range X" for
    // ReadRangeX), an auto-numbered form ("Sequence1"), or — for the uix N-prefixed
    // activities — the type name without its leading N ("Click" for NClick).
    private static bool MatchesTypeDefault(string normalized, string type) {
        if (string.IsNullOrWhiteSpace(type)) {
            return false;
        }

        if (IsTypeWithOptionalDigits(normalized, type)) {
            return true;
        }

        if (type.Length <= 1 || type[0] != 'N' || !char.IsUpper(type[1])) {
            return false;
        }

        var stripped = type[1..];
        return string.Equals(normalized, stripped, StringComparison.OrdinalIgnoreCase)
            || IsTypeWithOptionalDigits(normalized, stripped);
    }

    private static bool IsTypeWithOptionalDigits(string normalized, string type) {
        if (string.Equals(normalized, type, StringComparison.OrdinalIgnoreCase)) {
            return true;
        }

        return normalized.Length > type.Length
            && normalized.StartsWith(type, StringComparison.OrdinalIgnoreCase)
            && normalized.AsSpan(type.Length).ToArray().All(char.IsDigit);
    }

    private static string NormalizeForCompare(string value) {
        var builder = new StringBuilder(value.Length);
        foreach (var c in value) {
            if (!char.IsWhiteSpace(c) && c != '_') {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    private static string Describe(ActivityModel activity) {
        var label = string.IsNullOrWhiteSpace(activity.DisplayName) ? "<no DisplayName>" : activity.DisplayName;
        return $"'{label}' ({activity.Type} at {activity.Id})";
    }

    // Library public arguments deliberately drop the directional prefixes and become
    // activity property names (library-authoring-guide § The Public-Workflow Contract).
    private static bool IsLibrary(UiPathProjectModel model) =>
        string.Equals(model.OutputType, "Library", StringComparison.OrdinalIgnoreCase);

    // ------------------------------------------------------------ Argument naming

    // Coded .cs entry parameters are out of scope for this rule: the guide's own worked
    // examples use camelCase C# parameters (Execute(string inputPath)), and the directional
    // prefixes belong to the XAML argument surface that InvokeWorkflowFile binds.
    private static void LintArgumentPrefixes(string fileName, IReadOnlyList<ArgumentModel> arguments, List<Gap> gaps) {
        var offenders = new List<string>();
        foreach (var argument in arguments) {
            if (string.IsNullOrWhiteSpace(argument.Name) || HasDirectionalPrefix(argument.Name)) {
                continue;
            }

            offenders.Add($"{argument.Name} (expected {ExpectedPrefix(argument.Direction)}{argument.Name})");
        }

        if (offenders.Count == 0) {
            return;
        }

        gaps.Add(new Gap {
            Id = $"argument-prefix-convention:{fileName}",
            Severity = Gap.Info,
            // Parsed x:Property members: structural, not inferred from a type name.
            Confidence = Gap.ConfidenceHigh,
            Category = Gap.CategoryReadability,
            Message = $"'{fileName}' declares {offenders.Count} argument(s) without the directional prefix: "
                + $"{Summarize(offenders)}. Process workflow arguments use in_ / out_ / io_ so data flow is "
                + "visible at every invocation site.",
            TargetFile = fileName,
            SuggestedTool = "manage_workflow_data",
            SuggestedAction = "Rename each argument with manage_workflow_data (operation:rename), then update the "
                + "InvokeWorkflowFile bindings that reference the old name."
        });
    }

    internal static bool HasDirectionalPrefix(string name) =>
        name.StartsWith("in_", StringComparison.Ordinal)
        || name.StartsWith("out_", StringComparison.Ordinal)
        || name.StartsWith("io_", StringComparison.Ordinal);

    private static string ExpectedPrefix(string direction) => direction switch {
        "Out" => "out_",
        "In/Out" => "io_",
        _ => "in_"
    };

    // ----------------------------------------------------------- Complexity metrics

    private static void LintComplexity(WorkflowModel workflow, List<Gap> gaps) {
        if (workflow.Activities.Count > MaxWorkflowActivityCount) {
            gaps.Add(new Gap {
                Id = $"workflow-activity-count:{workflow.FileName}",
                Severity = Gap.Info,
                Confidence = Gap.ConfidenceHigh,
                Category = Gap.CategoryReadability,
                Message = $"'{workflow.FileName}' contains {workflow.Activities.Count} activities "
                    + $"(guideline: split a workflow above {MaxWorkflowActivityCount}).",
                TargetFile = workflow.FileName,
                SuggestedTool = "add_xaml_workflow",
                SuggestedAction = "Extract a cohesive step into its own workflow file and call it with InvokeWorkflowFile."
            });
        }

        var deepest = workflow.Activities.MaxBy(a => a.Depth);
        if (deepest is not null && deepest.Depth + 1 > MaxNestingLevels) {
            gaps.Add(new Gap {
                Id = $"deep-container-nesting:{workflow.FileName}",
                Severity = Gap.Info,
                Confidence = Gap.ConfidenceHigh,
                Category = Gap.CategoryReadability,
                Message = $"'{workflow.FileName}' nests {deepest.Depth + 1} levels deep at {Describe(deepest)} "
                    + $"(guideline: keep nesting at or below {MaxNestingLevels} levels; Rule 24 body wraps count).",
                TargetFile = workflow.FileName,
                SuggestedTool = "add_xaml_workflow",
                SuggestedAction = "Lift the deepest branch into its own workflow and invoke it, or flatten the control flow."
            });
        }

        foreach (var container in workflow.Activities.Where(a => a.Children.Count > MaxContainerActivityCount)) {
            gaps.Add(new Gap {
                Id = $"container-activity-count:{workflow.FileName}:{container.Id}",
                Severity = Gap.Info,
                Confidence = Gap.ConfidenceHigh,
                Category = Gap.CategoryReadability,
                Message = $"{Describe(container)} in '{workflow.FileName}' holds {container.Children.Count} direct "
                    + $"activities (guideline: keep a container body at or below {MaxContainerActivityCount}).",
                TargetFile = workflow.FileName,
                SuggestedTool = "add_xaml_workflow",
                SuggestedAction = "Group the body into named sub-sequences, or extract it into a child workflow."
            });
        }
    }

    // ------------------------------------------------------------------- Rule 24

    /// <remarks>
    /// Only single-body containers are checkable from the parsed model. Slot-named bodies
    /// (<c>If.Then</c>/<c>If.Else</c>, <c>Switch.Default</c> and its cases, <c>TryCatch.Try</c>/
    /// <c>Catch</c>/<c>Finally</c>, <c>ForEach.Body</c>) are transparent to
    /// <see cref="XamlActivityLocator"/> and do not consume depth, so a wrapped branch and a bare
    /// one are byte-identical in <see cref="ActivityModel"/> — those need parser support.
    /// </remarks>
    private static void LintContainerBodies(WorkflowModel workflow, List<Gap> gaps) {
        foreach (var container in workflow.Activities) {
            if (!SingleBodyContainers.Contains(container.Type)) {
                continue;
            }

            if (container.Children.Count >= 2) {
                gaps.Add(new Gap {
                    Id = $"unwrapped-container-body:{workflow.FileName}:{container.Id}",
                    Severity = Gap.Warning,
                    // Child count is structural; only the container type is name-matched.
                    Confidence = Gap.ConfidenceMedium,
                    Category = Gap.CategoryReadability,
                    Message = $"{Describe(container)} in '{workflow.FileName}' has {container.Children.Count} direct "
                        + "children, but its body slot holds a single Activity. Wrap them in one Sequence (Rule 24).",
                    TargetFile = workflow.FileName,
                    SuggestedTool = "edit_workflow_file",
                    SuggestedAction = "Insert a single Sequence as the container body, move the children into it, "
                        + "then run validate_project(build:true) — validate and build both accept the bare form."
                });
                continue;
            }

            if (container.Children.Count == 1
                && !string.Equals(container.Children[0].Type, "Sequence", StringComparison.Ordinal)) {
                gaps.Add(new Gap {
                    Id = $"container-body-not-sequence:{workflow.FileName}:{container.Id}",
                    Severity = Gap.Info,
                    // Type-name dependent and namespace-blind: hint only.
                    Confidence = Gap.ConfidenceLow,
                    Category = Gap.CategoryReadability,
                    Message = $"{Describe(container)} in '{workflow.FileName}' has a bare "
                        + $"'{container.Children[0].Type}' body. Hint: Rule 24 wraps every container body in a Sequence, "
                        + "even a single-activity one; validate and build both accept the bare form, so verify in the file.",
                    TargetFile = workflow.FileName,
                    SuggestedTool = "edit_workflow_file",
                    SuggestedAction = "Wrap the body in a Sequence, then run validate_project(build:false, pack:false)."
                });
            }
        }
    }

    // ------------------------------------------------------- Description coverage

    private static void LintDescriptionCoverage(UiPathProjectModel model, List<Gap> gaps) {
        var scoped = model.Workflows.Where(w => !w.HasParseError).ToList();
        if (scoped.Count < MinWorkflowsForProjectAdvice) {
            return;
        }

        var described = scoped.Count(w => !string.IsNullOrWhiteSpace(w.Description));
        if (described * 2 >= scoped.Count) {
            return;
        }

        gaps.Add(new Gap {
            Id = "description-coverage-low",
            Severity = Gap.Info,
            // WorkflowModel.Description is the parsed root sap2010:Annotation.AnnotationText.
            Confidence = Gap.ConfidenceHigh,
            Category = Gap.CategoryReadability,
            Message = $"Only {described} of {scoped.Count} workflows carry a workflow-level description. Studio shows "
                + "the root annotation as the workflow description, so undocumented workflows stay opaque to the "
                + "next reader.",
            SuggestedTool = "edit_workflow_file",
            SuggestedAction = "Add a sap2010:Annotation.AnnotationText to each undocumented workflow's root Activity."
        });
    }

    // ------------------------------------------------------- REFramework conformance

    private static void LintReFramework(UiPathProjectModel model, List<Gap> gaps) {
        var byCanonicalName = new Dictionary<string, WorkflowModel>(StringComparer.OrdinalIgnoreCase);
        foreach (var workflow in model.Workflows.Where(w => !w.HasParseError)) {
            foreach (var canonical in ReFrameworkFiles) {
                if (string.Equals(workflow.FileName, canonical, StringComparison.OrdinalIgnoreCase)) {
                    byCanonicalName.TryAdd(canonical, workflow);
                }
            }
        }

        var globalHandlers = model.Workflows
            .Where(w => (w.FileName ?? string.Empty).Contains("GlobalHandler", StringComparison.OrdinalIgnoreCase))
            .ToList();

        // Two of the four state files is the smallest unambiguous signal; a lone
        // Framework/InitAllSettings.xaml is not a REFramework project.
        if (byCanonicalName.Count < 2) {
            LintStandaloneGlobalHandler(model, globalHandlers, gaps);
            return;
        }

        foreach (var canonical in ReFrameworkFiles) {
            if (byCanonicalName.ContainsKey(canonical)) {
                continue;
            }

            gaps.Add(new Gap {
                Id = $"reframework-missing-file:{canonical}",
                Severity = Gap.Warning,
                // Classification is by canonical file name, not by parsed state-machine shape.
                Confidence = Gap.ConfidenceMedium,
                Category = Gap.CategoryReadability,
                Message = $"The project looks like REFramework but '{canonical}' is missing. The four state files are "
                    + $"{string.Join(", ", ReFrameworkFiles)}.",
                TargetFile = "Framework/" + canonical,
                SuggestedTool = "add_xaml_workflow",
                SuggestedAction = $"Add 'Framework/{canonical}' and wire it into the matching state in Main.xaml, "
                    + "or drop the REFramework shape if this process is not transactional."
            });
        }

        LintTransactionArgument("GetTransactionData.xaml", "out_", "reframework-no-get-transaction-item",
            "GetTransactionData must return the next item through an out_TransactionItem argument "
            + "(Nothing signals End Process).",
            byCanonicalName, gaps);

        LintTransactionArgument("SetTransactionStatus.xaml", "in_", "reframework-no-set-transaction-status",
            "SetTransactionStatus must receive the item through an in_TransactionItem argument; Success, "
            + "BusinessRuleException and System Exception all route through it.",
            byCanonicalName, gaps);

        // reframework-guide and error-handling-guide §8 both forbid the combination: the
        // framework's per-state Try/Catch and a Global Exception Handler fight over the fault.
        foreach (var handler in globalHandlers) {
            gaps.Add(new Gap {
                Id = $"reframework-global-handler-conflict:{handler.FileName}",
                Severity = Gap.Warning,
                // File-name detection only; project.json runtimeOptions is not in the model.
                Confidence = Gap.ConfidenceLow,
                Category = Gap.CategoryReadability,
                Message = $"'{handler.FileName}' looks like a Global Exception Handler in a REFramework project. "
                    + "Hint: the framework's per-state Try/Catch and a Global Exception Handler retry the same fault — "
                    + "pick one strategy. Verify before removing either.",
                TargetFile = handler.FileName,
                SuggestedTool = "patch_project_json",
                SuggestedAction = "Keep REFramework: clear runtimeOptions.exceptionHandlerWorkflow with "
                    + "patch_project_json (set_exception_handler), then delete the handler file."
            });
        }
    }

    private static void LintTransactionArgument(
        string canonicalFile,
        string requiredPrefix,
        string gapId,
        string message,
        IReadOnlyDictionary<string, WorkflowModel> byCanonicalName,
        List<Gap> gaps) {

        if (!byCanonicalName.TryGetValue(canonicalFile, out var workflow)) {
            return;
        }

        var wired = workflow.Arguments.Any(a =>
            a.Name.StartsWith(requiredPrefix, StringComparison.OrdinalIgnoreCase)
            && a.Name.Contains("TransactionItem", StringComparison.OrdinalIgnoreCase));
        if (wired) {
            return;
        }

        gaps.Add(new Gap {
            Id = gapId,
            Severity = Gap.Warning,
            Confidence = Gap.ConfidenceHigh,
            Category = Gap.CategoryReadability,
            Message = $"'{canonicalFile}' declares no {requiredPrefix}TransactionItem argument. {message}",
            TargetFile = canonicalFile,
            SuggestedTool = "manage_workflow_data",
            SuggestedAction = "Add the argument with manage_workflow_data (kind:argument), then update every matching "
                + "InvokeWorkflowFile binding in Main.xaml — a partial rename surfaces as argument-mismatch errors."
        });
    }

    // Global Exception Handler: one per project, centralized last-line logging plus
    // screenshot-on-error, and not available for Library projects (error-handling-guide §8).
    private static void LintStandaloneGlobalHandler(
        UiPathProjectModel model,
        IReadOnlyList<WorkflowModel> globalHandlers,
        List<Gap> gaps) {

        if (globalHandlers.Count > 0
            || model.Workflows.Count < MinWorkflowsForProjectAdvice
            || !string.Equals(model.OutputType, "Process", StringComparison.OrdinalIgnoreCase)) {
            return;
        }

        gaps.Add(new Gap {
            Id = "no-global-handler",
            Severity = Gap.Info,
            // Output type is from project.json; the absence check is a file-name scan.
            Confidence = Gap.ConfidenceMedium,
            Category = Gap.CategoryReadability,
            Message = "This Process project has no Global Exception Handler for centralized last-line logging and "
                + "screenshot-on-error. It complements local Try/Catch; it does not replace it. Do not add one if "
                + "the project later adopts REFramework.",
            SuggestedTool = "add_xaml_workflow",
            SuggestedAction = "Add GlobalHandlerX.xaml with the errorInfo (In, ExceptionHandlerArgs) and result "
                + "(Out, ErrorAction) arguments, then register it with patch_project_json (set_exception_handler)."
        });
    }

    private static string Summarize(IReadOnlyList<string> offenders) =>
        offenders.Count <= MaxReportedOffenders
            ? string.Join(", ", offenders)
            : $"{string.Join(", ", offenders.Take(MaxReportedOffenders))} (+{offenders.Count - MaxReportedOffenders} more)";
}
