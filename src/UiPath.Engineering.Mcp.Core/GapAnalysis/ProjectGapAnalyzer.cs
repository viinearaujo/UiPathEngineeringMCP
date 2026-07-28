using UiPath.Engineering.Mcp.Core.Models;
using UiPath.Engineering.Mcp.Core.Parsing;

namespace UiPath.Engineering.Mcp.Core.GapAnalysis;

/// <summary>
/// Deterministic, model-only hygiene rules over a <see cref="UiPathProjectModel"/>, plus a
/// cross-check against the project's <see cref="ImplementationPlan"/>. Each gap names the
/// MCP tool that can fix it so an agent can drive the analyze → plan → implement → verify
/// loop autonomously.
/// </summary>
public static class ProjectGapAnalyzer {
    public static List<Gap> Analyze(UiPathProjectModel model, ImplementationPlan? plan = null) {
        var gaps = new List<Gap>();
        var graph = DependencyGraphBuilder.Build(model.Workflows, model.MainWorkflow);
        var workflowsByName = new Dictionary<string, WorkflowModel>(StringComparer.OrdinalIgnoreCase);
        foreach (var workflow in model.Workflows) {
            workflowsByName.TryAdd(workflow.FileName, workflow);
        }

        var entry = model.MainWorkflow is null ? null : workflowsByName.GetValueOrDefault(model.MainWorkflow);

        // Entry point rules.
        if (string.IsNullOrWhiteSpace(model.MainWorkflow)) {
            gaps.Add(new Gap {
                Id = "no-entry-point",
                Severity = Gap.Error,
                Category = "project",
                Message = "project.json does not declare an entry point ('main').",
                SuggestedTool = "write_workflow_file",
                SuggestedAction = "Create Main.xaml and set it as the entry point in project.json."
            });
        } else if (entry is null) {
            gaps.Add(new Gap {
                Id = "entry-point-missing",
                Severity = Gap.Error,
                Category = "project",
                Message = $"Entry point '{model.MainWorkflow}' is declared in project.json but the file is missing on disk.",
                TargetFile = model.MainWorkflow,
                SuggestedTool = "write_workflow_file",
                SuggestedAction = $"Create '{model.MainWorkflow}' or fix the 'main' setting in project.json."
            });
        }

        // Entry points declared in project.json but missing on disk.
        foreach (var entryPoint in model.EntryPoints) {
            var fileName = Path.GetFileName(entryPoint);
            if (!workflowsByName.ContainsKey(fileName)) {
                gaps.Add(new Gap {
                    Id = $"declared-entry-point-missing:{entryPoint}",
                    Severity = Gap.Error,
                    Category = "project",
                    Message = $"'{entryPoint}' is declared in project.json entryPoints but the file is missing on disk.",
                    TargetFile = entryPoint,
                    SuggestedTool = "write_workflow_file",
                    SuggestedAction = $"Create '{entryPoint}' or remove it from project.json entryPoints."
                });
            }
        }

        // Orphan workflows: never invoked and not an entry point. Test workflows are
        // standalone by design and exempt.
        foreach (var orphan in graph.Orphans.Where(o => !IsTestWorkflow(o))) {
            gaps.Add(new Gap {
                Id = $"orphan-workflow:{orphan}",
                Severity = Gap.Warning,
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
                SuggestedTool = "write_workflow_file",
                SuggestedAction = $"Create '{edge.Target}' or fix the InvokeWorkflowFile in '{edge.Source}'."
            });
        }

        // Entry workflow resilience/observability.
        if (entry is not null && entry.ExceptionHandlers.Count == 0) {
            gaps.Add(new Gap {
                Id = "entry-no-exception-handling",
                Severity = Gap.Warning,
                Category = "resilience",
                Message = $"Entry workflow '{entry.FileName}' has no TryCatch exception handling.",
                TargetFile = entry.FileName,
                SuggestedTool = "edit_workflow_activity",
                SuggestedAction = "Wrap the entry workflow body in a TryCatch."
            });
        }

        if (entry is not null && entry.LogMessages.Count == 0) {
            gaps.Add(new Gap {
                Id = "entry-no-logging",
                Severity = Gap.Info,
                Category = "observability",
                Message = $"Entry workflow '{entry.FileName}' contains no LogMessage activities.",
                TargetFile = entry.FileName,
                SuggestedTool = "edit_workflow_activity",
                SuggestedAction = "Add LogMessage activities to the entry workflow."
            });
        }

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

        // Testing hygiene: satisfied by xaml test workflows or coded files with 'Test' in the name.
        if (!model.Workflows.Any(w => IsTestWorkflow(w.FileName))
            && !model.CodedWorkflows.Any(c => IsTestWorkflow(c.FileName))) {
            gaps.Add(new Gap {
                Id = "no-test-workflows",
                Severity = Gap.Info,
                Category = "testing",
                Message = "The project contains no test workflows.",
                SuggestedAction = "Add at least one test workflow (file name containing 'Test')."
            });
        }

        // Plan cross-check: pending/in_progress tasks vs. the files they should produce.
        if (plan is not null) {
            foreach (var task in plan.Tasks.Where(t => t.Status is PlanTask.Pending or PlanTask.InProgress && t.TargetFiles.Count > 0)) {
                var missing = task.TargetFiles.Where(f => !FileExists(model.ProjectPath, f)).ToList();
                if (missing.Count == 0) {
                    gaps.Add(new Gap {
                        Id = $"plan-task-possibly-complete:{task.Id}",
                        Severity = Gap.Info,
                        Category = "plan",
                        Message = $"Task '{task.Id}' ({task.Title}) is '{task.Status}' but all its target files already exist.",
                        SuggestedTool = "verify_work",
                        SuggestedAction = $"Run verify_work for task '{task.Id}' to confirm and mark it done."
                    });
                } else {
                    gaps.Add(new Gap {
                        Id = $"plan-artifact-missing:{task.Id}",
                        Severity = Gap.Warning,
                        Category = "plan",
                        Message = $"Task '{task.Id}' ({task.Title}) is '{task.Status}' but planned file(s) are missing: {string.Join(", ", missing)}.",
                        TargetFile = missing[0],
                        SuggestedTool = "write_workflow_file",
                        SuggestedAction = "Create the planned file(s), or adjust the plan if they are no longer needed."
                    });
                }
            }
        }

        return gaps;
    }

    private static bool IsTestWorkflow(string fileName) =>
        fileName.Contains("test", StringComparison.OrdinalIgnoreCase);

    private static bool FileExists(string projectPath, string relativePath) =>
        File.Exists(Path.Combine(projectPath, relativePath.Replace('/', Path.DirectorySeparatorChar)));
}
