using UiPath.Engineering.Mcp.Core.Abstractions;
using UiPath.Engineering.Mcp.Core.Docs;
using UiPath.Engineering.Mcp.Core.Models;
using UiPath.Engineering.Mcp.Core.Parsing;

namespace UiPath.Engineering.Mcp.Core.GapAnalysis;

/// <summary>
/// Deterministic, model-only hygiene rules over a <see cref="UiPathProjectModel"/>, plus a
/// cross-check against the project's <see cref="ImplementationPlan"/>. Each gap names the
/// MCP tool that can fix it so an agent can drive the analyze → plan → implement → verify
/// loop autonomously.
/// </summary>
/// <remarks>
/// <see cref="XamlWorkflowParser"/> matches on XAML local names only and is namespace-blind,
/// and several rules classify by file name or substring. Every gap therefore carries a
/// <see cref="Gap.Confidence"/> alongside its <see cref="Gap.Severity"/>: severity says how
/// much the finding matters when it is real, confidence says how much evidence backs it.
/// Treat <see cref="Gap.ConfidenceLow"/> gaps as hints to verify, not as asserted facts.
/// </remarks>
public static class ProjectGapAnalyzer {
    /// <summary>
    /// Analyzes the model. <paramref name="filesystem"/> is required for the plan
    /// cross-check, which verifies planned artifacts on disk; without it the two
    /// <c>plan-*</c> rules are skipped rather than guessed at.
    /// </summary>
    public static List<Gap> Analyze(
        UiPathProjectModel model,
        ImplementationPlan? plan = null,
        IReadOnlyList<DocsFinding>? docsFindings = null,
        IFilesystemProvider? filesystem = null) {

        var gaps = new List<Gap>();
        var graph = DependencyGraphBuilder.Build(model.Workflows, model.MainWorkflow, model.CodedWorkflows);
        var workflowsByIdentity = new Dictionary<string, WorkflowModel>(StringComparer.OrdinalIgnoreCase);
        foreach (var workflow in model.Workflows) {
            workflowsByIdentity.TryAdd(WorkflowPath.Identity(workflow), workflow);
        }

        var entry = model.MainWorkflow is null
            ? null
            : WorkflowPath.Find(model.Workflows, model.MainWorkflow) ?? workflowsByIdentity.GetValueOrDefault(WorkflowPath.NormalizeRef(model.MainWorkflow));

        // Entry point rules.
        if (string.IsNullOrWhiteSpace(model.MainWorkflow)) {
            gaps.Add(new Gap {
                Id = "no-entry-point",
                Severity = Gap.Error,
                Category = "project",
                Message = "project.json does not declare an entry point ('main').",
                SuggestedTool = "add_coded_workflow",
                SuggestedAction = "Create Main.xaml and set it as the entry point in project.json."
            });
        } else if (entry is null) {
            gaps.Add(new Gap {
                Id = "entry-point-missing",
                Severity = Gap.Error,
                Category = "project",
                Message = $"Entry point '{model.MainWorkflow}' is declared in project.json but the file is missing on disk.",
                TargetFile = model.MainWorkflow,
                SuggestedTool = "add_coded_workflow",
                SuggestedAction = $"Create '{model.MainWorkflow}' or fix the 'main' setting in project.json."
            });
        }

        // Entry points declared in project.json but missing on disk.
        foreach (var entryPoint in model.EntryPoints) {
            if (WorkflowPath.Find(model.Workflows, entryPoint) is null
                && !model.CodedWorkflows.Any(c =>
                    string.Equals(c.FileName, Path.GetFileName(entryPoint), StringComparison.OrdinalIgnoreCase)
                    || string.Equals(
                        WorkflowPath.NormalizeRef(c.FilePath),
                        WorkflowPath.NormalizeRef(entryPoint),
                        StringComparison.OrdinalIgnoreCase))) {
                gaps.Add(new Gap {
                    Id = $"declared-entry-point-missing:{entryPoint}",
                    Severity = Gap.Error,
                    Category = "project",
                    Message = $"'{entryPoint}' is declared in project.json entryPoints but the file is missing on disk.",
                    TargetFile = entryPoint,
                    SuggestedTool = "add_coded_workflow",
                    SuggestedAction = $"Create '{entryPoint}' or remove it from project.json entryPoints."
                });
            }
        }

        // Orphan workflows: never invoked and not an entry point. Test workflows are
        // standalone by design and exempt — the exemption is name-based, so the gap is
        // graded medium even though the invoke graph itself is structural.
        foreach (var orphan in graph.Orphans.Where(o => !IsTestWorkflow(o, model))) {
            gaps.Add(new Gap {
                Id = $"orphan-workflow:{orphan}",
                Severity = Gap.Warning,
                Confidence = Gap.ConfidenceMedium,
                Category = "structure",
                Message = $"'{orphan}' is never invoked and is not an entry point.",
                TargetFile = orphan,
                SuggestedAction = "Invoke it from another workflow, or remove it from the project."
            });
        }

        // Referenced (invoked) workflow files missing on disk.
        foreach (var edge in graph.Edges.Where(e => !e.IsResolved)) {
            gaps.Add(new Gap {
                Id = $"unresolved-invoke:{edge.Source}->{edge.Target}",
                Severity = Gap.Error,
                Category = "structure",
                Message = $"'{edge.Source}' invokes '{edge.Target}', which does not exist in the project.",
                TargetFile = edge.Target,
                SuggestedTool = "add_coded_workflow",
                SuggestedAction = $"Create '{edge.Target}' or fix the InvokeWorkflowFile in '{edge.Source}'."
            });
        }

        // Entry workflow resilience/observability (hybrid REFramework XAML still matters).
        // Absence of a local-name match is stronger evidence than presence, but it is still
        // namespace-blind, so both stay medium.
        if (entry is not null && entry.ExceptionHandlers.Count == 0) {
            gaps.Add(new Gap {
                Id = "entry-no-exception-handling",
                Severity = Gap.Warning,
                Confidence = Gap.ConfidenceMedium,
                Category = "resilience",
                Message = $"Entry workflow '{entry.FileName}' has no TryCatch exception handling.",
                TargetFile = entry.FileName,
                SuggestedTool = "insert_activities",
                SuggestedAction = "Wrap the entry workflow body in a TryCatch."
            });
        }

        if (entry is not null && entry.LogMessages.Count == 0) {
            gaps.Add(new Gap {
                Id = "entry-no-logging",
                Severity = Gap.Info,
                Confidence = Gap.ConfidenceMedium,
                Category = "observability",
                Message = $"Entry workflow '{entry.FileName}' contains no LogMessage activities.",
                TargetFile = entry.FileName,
                SuggestedTool = "insert_activities",
                SuggestedAction = "Add LogMessage activities to the entry workflow."
            });
        }

        LintCodedWorkflows(model, gaps);
        LintXamlBusinessLogic(model, gaps);
        ReadabilityLinter.Lint(model, gaps);

        // Documentation hygiene.
        foreach (var workflow in model.Workflows.Where(w => string.IsNullOrWhiteSpace(w.Description))) {
            gaps.Add(new Gap {
                Id = $"workflow-no-description:{workflow.FileName}",
                Severity = Gap.Info,
                Category = "documentation",
                Message = $"'{workflow.FileName}' has no description (workflow-level annotation).",
                TargetFile = workflow.FileName,
                SuggestedAction = "Add a workflow-level annotation describing what the workflow does."
            });
        }

        // Testing hygiene: XAML files named *Test* or coded files with kind=test. Name- and
        // kind-based classification only, so a miss is a hint rather than an asserted fact.
        if (!model.Workflows.Any(w => IsTestWorkflow(WorkflowPath.Identity(w), model))
            && !model.CodedWorkflows.Any(c => XamlCodedInvokeBoundary.EffectiveKind(c) == CodedFileKind.Test)) {
            gaps.Add(new Gap {
                Id = "no-test-workflows",
                Severity = Gap.Info,
                Confidence = Gap.ConfidenceLow,
                Category = "testing",
                Message = "The project contains no test workflows.",
                SuggestedTool = "add_coded_workflow",
                SuggestedAction = "Add a coded test case with add_coded_workflow kind=test (registered in fileInfoCollection)."
            });
        }

        gaps.AddRange(XamlCodedInvokeBoundary.Lint(model));

        // Plan cross-check: pending/in_progress tasks vs. the files they should produce.
        if (plan is not null && filesystem is not null) {
            foreach (var task in plan.Tasks.Where(t => t.Status is PlanTask.Pending or PlanTask.InProgress && t.TargetFiles.Count > 0)) {
                var missing = task.TargetFiles.Where(f => !FileExists(filesystem, model.ProjectPath, f)).ToList();
                if (missing.Count == 0) {
                    gaps.Add(new Gap {
                        Id = $"plan-task-possibly-complete:{task.Id}",
                        Severity = Gap.Info,
                        Category = "plan",
                        Message = $"Task '{task.Id}' ({task.Title}) is '{task.Status}' but all its target files already exist.",
                        SuggestedTool = "update_plan_task",
                        SuggestedAction = $"Run validate_project(build:false, pack:false), then update_plan_task for '{task.Id}' to mark it done."
                    });
                } else {
                    gaps.Add(new Gap {
                        Id = $"plan-artifact-missing:{task.Id}",
                        Severity = Gap.Warning,
                        Category = "plan",
                        Message = $"Task '{task.Id}' ({task.Title}) is '{task.Status}' but planned file(s) are missing: {string.Join(", ", missing)}.",
                        TargetFile = missing[0],
                        SuggestedTool = "add_coded_workflow",
                        SuggestedAction = "Create the planned file(s), or adjust the plan if they are no longer needed."
                    });
                }
            }
        }

        if (docsFindings is not null) {
            foreach (var finding in docsFindings.Where(f => f.Severity == DocsFinding.Error)) {
                gaps.Add(new Gap {
                    Id = $"docs:{finding.Code}:{finding.TargetFile ?? finding.Message}",
                    Severity = Gap.Error,
                    Category = "docs",
                    Message = finding.Message,
                    TargetFile = finding.TargetFile,
                    SuggestedTool = finding.SuggestedTool,
                    SuggestedAction = finding.FixHint
                });
            }
        }

        return gaps;
    }

    private static void LintCodedWorkflows(UiPathProjectModel model, List<Gap> gaps) {
        foreach (var coded in model.CodedWorkflows) {
            if (XamlCodedInvokeBoundary.EffectiveKind(coded) != CodedFileKind.Workflow) {
                continue;
            }

            // Roslyn TryStatementSyntax scan: structural, not a substring match.
            if (coded.EntryHasTryCatch == false) {
                gaps.Add(new Gap {
                    Id = $"coded-no-exception-handling:{coded.FileName}",
                    Severity = Gap.Warning,
                    Category = "resilience",
                    Message = $"Coded workflow '{coded.FileName}' entry method has no try/catch.",
                    TargetFile = coded.FileName,
                    SuggestedTool = "edit_workflow_file",
                    SuggestedAction = "Wrap the coded workflow entry method in try/catch."
                });
            }

            // Substring heuristic: a comment containing "log." satisfies it. Hint only.
            if (coded.EntryHasLog == false) {
                gaps.Add(new Gap {
                    Id = $"coded-no-logging:{coded.FileName}",
                    Severity = Gap.Info,
                    Confidence = Gap.ConfidenceLow,
                    Category = "observability",
                    Message = $"Coded workflow '{coded.FileName}' entry method has no Log(...), LogMessage, or log. call.",
                    TargetFile = coded.FileName,
                    SuggestedTool = "edit_workflow_file",
                    SuggestedAction = "Add Log(...) to the coded workflow entry method."
                });
            }
        }
    }

    private static void LintXamlBusinessLogic(UiPathProjectModel model, List<Gap> gaps) {
        foreach (var workflow in model.Workflows) {
            if (IsFrameworkOrExemptXaml(workflow)) {
                continue;
            }

            if (ClassifyBusinessLogicActivity(workflow.Activities) is not { } evidence) {
                continue;
            }

            gaps.Add(new Gap {
                Id = $"xaml-business-logic:{workflow.FileName}",
                Severity = Gap.Info,
                // Namespace-blind local-name matching plus substring classification: a
                // preference to verify, not a violation. ui:Click, uix:NClick and a mobile
                // Click are indistinguishable in the parsed model.
                Confidence = evidence,
                Category = "boundary",
                Message = $"'{workflow.FileName}' appears to contain Excel, HTTP, Mail, or UI activities. "
                    + "Hint: activity types are matched on XAML local names only, so confirm against the file — "
                    + "if the work is real, prefer a coded workflow and keep XAML as a REFramework/Invoke shell.",
                TargetFile = workflow.FileName,
                SuggestedTool = "add_coded_workflow",
                SuggestedAction = "Move business logic into a coded workflow; keep this XAML as a REFramework/Invoke shell."
            });
        }
    }

    private static bool IsFrameworkOrExemptXaml(WorkflowModel workflow) {
        var name = workflow.FileName ?? string.Empty;
        var path = workflow.FilePath ?? string.Empty;
        var nameOnly = Path.GetFileNameWithoutExtension(name) ?? string.Empty;
        if (nameOnly.Equals("Main", StringComparison.OrdinalIgnoreCase)) {
            return true;
        }

        if (name.Contains("Framework", StringComparison.OrdinalIgnoreCase)
            || path.Contains("Framework", StringComparison.OrdinalIgnoreCase)) {
            return true;
        }

        return IsTestWorkflow(WorkflowPath.Identity(workflow), null, workflow.FilePath)
            || IsTestWorkflow(workflow.FileName ?? string.Empty)
            || nameOnly.Contains("_Test", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns the confidence of the strongest business-logic evidence in
    /// <paramref name="activities"/>, or null when none matched.
    /// </summary>
    private static string? ClassifyBusinessLogicActivity(IReadOnlyList<ActivityModel> activities) {
        string? best = null;
        foreach (var activity in activities) {
            var evidence = ClassifyBusinessLogicActivity(activity.Type);
            if (evidence is null) {
                continue;
            }

            if (best is null || Gap.ConfidenceRank(evidence) > Gap.ConfidenceRank(best)) {
                best = evidence;
            }
        }

        return best;
    }

    private static string? ClassifyBusinessLogicActivity(string type) {
        if (string.IsNullOrEmpty(type)) {
            return null;
        }

        // Exact local-name match against known Excel/UIA activities. Stronger than a
        // substring, but still namespace-blind.
        if (type is "ReadRange" or "ReadRangeX" or "WriteRange" or "WriteRangeX"
            or "ForEachRow" or "ForEachExcelRow" or "UseExcelFile" or "ExcelApplicationScope"
            or "Click" or "TypeInto" or "NClick" or "NTypeInto"
            or "UseApplication" or "ApplicationCard" or "NApplicationCard"
            or "SendHotkey" or "NHotkey") {
            return Gap.ConfidenceMedium;
        }

        // Substring classification: a custom or unrelated activity whose name merely
        // contains one of these words is indistinguishable from the real thing.
        if (type.Contains("Excel", StringComparison.OrdinalIgnoreCase)
            || type.Contains("Http", StringComparison.OrdinalIgnoreCase)
            || type.Contains("Mail", StringComparison.OrdinalIgnoreCase)
            || type.Contains("Outlook", StringComparison.OrdinalIgnoreCase)) {
            return Gap.ConfidenceLow;
        }

        // uix N-prefixed UIA activities, matched loosely on a verb fragment.
        if (type.StartsWith('N') && type.Length > 1 && char.IsUpper(type[1])) {
            var isUia = type.Contains("Click", StringComparison.OrdinalIgnoreCase)
                || type.Contains("Type", StringComparison.OrdinalIgnoreCase)
                || type.Contains("Application", StringComparison.OrdinalIgnoreCase)
                || type.Contains("Hotkey", StringComparison.OrdinalIgnoreCase)
                || type.Contains("Keyboard", StringComparison.OrdinalIgnoreCase)
                || type.Contains("GetText", StringComparison.OrdinalIgnoreCase)
                || type.Contains("Check", StringComparison.OrdinalIgnoreCase)
                || type.Contains("Select", StringComparison.OrdinalIgnoreCase)
                || type.Contains("Hover", StringComparison.OrdinalIgnoreCase)
                || type.Contains("Image", StringComparison.OrdinalIgnoreCase)
                || type.Contains("Screenshot", StringComparison.OrdinalIgnoreCase)
                || type.Contains("Find", StringComparison.OrdinalIgnoreCase)
                || type.Contains("Element", StringComparison.OrdinalIgnoreCase);
            return isUia ? Gap.ConfidenceLow : null;
        }

        return null;
    }

    /// <summary>
    /// Test-workflow classification by file name shape, <c>Tests/</c> folder, or
    /// <c>fileInfoCollection</c> membership.
    /// </summary>
    /// <remarks>
    /// The stem tokens are matched on a name boundary rather than by substring or plain
    /// case-insensitive suffix. A bare <c>EndsWith("Test", IgnoreCase)</c> classifies
    /// <c>Latest.xaml</c>, <c>Attest.xaml</c> and <c>Contest.xaml</c> as tests, which then
    /// exempts real production workflows from orphan detection and counts them as test
    /// coverage. UiPath test cases are PascalCase or separator-joined
    /// (<c>TestLoginFlow.cs</c>, <c>InvoiceTests.xaml</c>, <c>Invoice_Test.xaml</c>), so a
    /// boundary match keeps all of those and drops the prose words. Folder and
    /// <c>fileInfoCollection</c> matching stays case-insensitive.
    /// </remarks>
    private static bool IsTestWorkflow(string fileName, UiPathProjectModel? model = null, string? filePath = null) {
        var normalized = WorkflowPath.NormalizeRef(fileName);
        var path = WorkflowPath.NormalizeRef(filePath ?? fileName);
        var stem = Path.GetFileNameWithoutExtension(normalized);
        if (HasTestToken(stem)
            || normalized.StartsWith("Tests/", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("/Tests/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("Tests/", StringComparison.OrdinalIgnoreCase)
            || path.Contains("/Tests/", StringComparison.OrdinalIgnoreCase)) {
            return true;
        }

        if (model?.FileInfoCollection is { Count: > 0 } collection) {
            return collection.Any(entry => {
                var item = WorkflowPath.NormalizeRef(entry);
                return string.Equals(item, normalized, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(Path.GetFileName(item), Path.GetFileName(normalized), StringComparison.OrdinalIgnoreCase);
            });
        }

        return false;
    }

    /// <summary>
    /// True when the file stem contains <c>Test</c> as a whole name component:
    /// <c>TestMain</c>, <c>InvoiceTests</c>, <c>Invoice_Test</c>, <c>test_invoice</c>.
    /// Prose words that merely end in those letters are single components and do not match —
    /// <c>Attest</c>, <c>Contest</c>, <c>Latest</c>, <c>LatestInvoice</c>.
    /// </summary>
    private static bool HasTestToken(string stem) {
        foreach (var component in SplitNameComponents(stem)) {
            if (component.StartsWith("Test", StringComparison.OrdinalIgnoreCase)) {
                return true;
            }
        }

        return false;
    }

    // Splits on separators and on PascalCase boundaries, so "Invoice_TestMain" yields
    // Invoice / Test / Main.
    private static IEnumerable<string> SplitNameComponents(string stem) {
        var start = 0;
        for (var i = 0; i < stem.Length; i++) {
            var c = stem[i];
            if (!char.IsLetterOrDigit(c)) {
                yield return stem[start..i];
                start = i + 1;
                continue;
            }

            if (i > start && char.IsUpper(c) && !char.IsUpper(stem[i - 1])) {
                yield return stem[start..i];
                start = i;
                continue;
            }

        // Acronym boundary: "HTTPClient" yields HTTP / Client.
        if (i > start && char.IsUpper(c) && i + 1 < stem.Length && char.IsLower(stem[i + 1])
            && char.IsUpper(stem[i - 1])) {
                yield return stem[start..i];
                start = i;
            }
        }

        yield return stem[start..];
    }

    /// <summary>
    /// Existence check for a plan target file, routed through the provider seam instead of
    /// <see cref="File.Exists"/> so it is testable with fakes. The path is combined
    /// lexically; containment is the provider's job (<c>EnsureAllowed</c>), and a refusal, a
    /// missing file, or an unresolvable path all count as missing rather than throwing out
    /// of the analysis.
    /// </summary>
    private static bool FileExists(IFilesystemProvider filesystem, string projectPath, string relativePath) {
        if (string.IsNullOrWhiteSpace(projectPath) || string.IsNullOrWhiteSpace(relativePath)) {
            return false;
        }

        string targetPath;
        try {
            targetPath = ProjectFilePolicy.CombineProject(projectPath, relativePath);
        } catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) {
            return false;
        }

        try {
            return filesystem.FileExists(targetPath);
        } catch (Exception ex) when (
            ex is UnauthorizedAccessException
            or FileNotFoundException
            or DirectoryNotFoundException
            or IOException) {
            return false;
        }
    }
}
