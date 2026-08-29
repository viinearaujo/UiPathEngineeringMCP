using System.ComponentModel;
using System.Diagnostics;
using ModelContextProtocol.Server;
using UiPath.Engineering.Mcp.Core.Abstractions;
using UiPath.Engineering.Mcp.Core.Models;
using UiPath.Engineering.Mcp.Core.Parsing;

namespace UiPath.Engineering.Mcp.Tools;

[McpServerToolType]
public sealed class GetWorkflowDependenciesTool {
    private readonly IFilesystemProvider _filesystem;
    private readonly IProjectModelBuilder _modelBuilder;

    public GetWorkflowDependenciesTool(IFilesystemProvider filesystem, IProjectModelBuilder modelBuilder) {
        _filesystem = filesystem;
        _modelBuilder = modelBuilder;
    }

    [McpServerTool(UseStructuredContent = true), Description("Shows the InvokeWorkflowFile dependency graph of a UiPath project. With workflowFile: the callers and callees of that workflow, each edge carrying the argument mappings passed at the invoke site. Without workflowFile: the full project edge list plus cycles, orphans (unreachable from Main), and unresolved targets.")]
    public async Task<ToolResult> GetWorkflowDependencies(
        [Description("Absolute path to the UiPath project directory (must contain project.json).")] string projectPath,
        [Description("Optional workflow file name (with or without .xaml). When omitted, the project-wide graph is returned.")] string? workflowFile = null,
        CancellationToken cancellationToken = default) {
        var sw = Stopwatch.StartNew();

        if (ToolResults.GuardProject(_filesystem, projectPath, sw) is { } guardFailure) {
            return guardFailure;
        }

        try {
            var model = await _modelBuilder.BuildAsync(projectPath, cancellationToken);
            var graph = DependencyGraphBuilder.Build(model.Workflows, model.MainWorkflow);

            if (workflowFile is null) {
                return ProjectWide(graph, sw);
            }

            var requestedName = Path.GetFileName(workflowFile);
            if (!requestedName.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)) {
                requestedName += ".xaml";
            }
            var workflow = model.Workflows.FirstOrDefault(w =>
                string.Equals(w.FileName, requestedName, StringComparison.OrdinalIgnoreCase));
            if (workflow is null) {
                return new ToolResult {
                    Status = "error",
                    Summary = $"Workflow '{requestedName}' not found.",
                    Errors = [$"Workflow '{requestedName}' was not found in project '{model.ProjectName}'."],
                    Data = new {
                        availableWorkflows = model.Workflows
                            .Select(w => w.FileName)
                            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                            .ToList()
                    },
                    DurationMs = sw.ElapsedMilliseconds
                };
            }

            var callers = graph.CallersIndex.TryGetValue(workflow.FileName, out var incoming)
                ? incoming
                : [];
            return ToolResults.Ok(
                $"Workflow '{workflow.FileName}': {callers.Count} caller(s), {workflow.InvokeWorkflows.Count} callee(s).",
                new {
                    workflow = workflow.FileName,
                    callers = callers.Select(c => new {
                        sourceWorkflow = c.Source,
                        displayName = c.DisplayName,
                        argumentMappings = MapArguments(c.ArgumentMappings)
                    }).ToList(),
                    callees = workflow.InvokeWorkflows.Select(i => new {
                        targetWorkflow = i.TargetWorkflow,
                        displayName = i.DisplayName,
                        argumentMappings = MapArguments(i.ArgumentMappings)
                    }).ToList()
                }, sw);
        } catch (Exception ex) {
            return ToolResults.FromException(ex, "Dependency analysis failed.", sw);
        }
    }

    private static ToolResult ProjectWide(DependencyGraphResult graph, Stopwatch sw) =>
        ToolResults.Ok(
            $"Dependency graph: {graph.Edges.Count} edge(s), {graph.Cycles.Count} cycle(s), " +
            $"{graph.Orphans.Count} orphan(s), {graph.Edges.Count(e => !e.IsResolved)} unresolved.",
            new {
                edges = graph.Edges.Select(e => new {
                    sourceWorkflow = e.Source,
                    targetWorkflow = e.Target,
                    displayName = e.DisplayName,
                    isResolved = e.IsResolved,
                    argumentMappings = MapArguments(e.ArgumentMappings)
                }).ToList(),
                cycles = graph.Cycles,
                orphans = graph.Orphans,
                unresolved = graph.Edges
                    .Where(e => !e.IsResolved)
                    .Select(e => new { sourceWorkflow = e.Source, targetWorkflow = e.Target })
                    .ToList()
            }, sw);

    private static List<object> MapArguments(List<ArgumentMappingModel> mappings) =>
        mappings.Select(m => (object)new {
            direction = m.Direction,
            targetArgument = m.TargetArgument,
            expression = m.Expression
        }).ToList();
}
