using System.ComponentModel;
using System.Diagnostics;
using ModelContextProtocol.Server;
using UiPath.Engineering.Mcp.Core.Abstractions;
using UiPath.Engineering.Mcp.Core.Models;
using UiPath.Engineering.Mcp.Core.Parsing;

namespace UiPath.Engineering.Mcp.Tools;

[McpServerToolType]
public sealed class GenerateDocumentationTool {
    private readonly IFilesystemProvider _filesystem;
    private readonly IProjectModelBuilder _modelBuilder;

    public GenerateDocumentationTool(IFilesystemProvider filesystem, IProjectModelBuilder modelBuilder) {
        _filesystem = filesystem;
        _modelBuilder = modelBuilder;
    }

    [McpServerTool(UseStructuredContent = true), Description("Generates structured documentation data for a UiPath project: metadata, per-workflow summaries, dependency graph, and risks. Next: analyze_project.")]
    public async Task<ToolResult> GenerateDocumentation(
        [Description("Absolute path to the UiPath project directory.")] string projectPath,
        CancellationToken cancellationToken = default) {
        var sw = Stopwatch.StartNew();

        if (ToolResults.GuardAllowedPath(_filesystem, projectPath, sw) is { } guardFailure) {
            return guardFailure;
        }

        var model = await _modelBuilder.BuildAsync(projectPath, cancellationToken);
        var graph = DependencyGraphBuilder.Build(model.Workflows, model.MainWorkflow);

        var data = new {
            Project = new {
                model.ProjectName,
                model.ProjectPath,
                model.MainWorkflow,
                model.Description,
                model.ReadmeSummary,
                Packages = model.Packages.Select(p => new { p.Id, p.Version }).ToList()
            },
            Workflows = model.Workflows.Select(w => new {
                w.FileName,
                w.IsMain,
                ArgumentCount = w.Arguments.Count,
                ArgumentNames = w.Arguments.Select(a => a.Name).ToList(),
                VariableCount = w.Variables.Count,
                ActivityOutline = w.Activities
                    .Where(a => a.ParentId is null)
                    .Select(ToActivityNode)
                    .ToList(),
                InvokedWorkflows = w.InvokeWorkflows.Select(i => i.TargetWorkflow).ToList(),
                LogMessageCount = w.LogMessages.Count,
                w.HasParseError,
                w.ParseError
            }).ToList(),
            DependencyGraph = new {
                Edges = graph.Edges.Select(e => new { e.Source, e.Target, e.IsResolved }).ToList(),
                graph.Cycles,
                graph.Orphans
            },
            model.Risks
        };

        return ToolResults.Ok(
            $"Documentation data generated for project '{model.ProjectName}' ({model.Workflows.Count} workflows, {model.Risks.Count} risks).",
            data, sw);
    }

    // The full activity hierarchy, nesting to any depth. Depth <= 1 truncated the outline to
    // the root plus its direct children, so anything inside an If/ForEach/TryCatch was
    // invisible in the generated docs.
    private static object ToActivityNode(ActivityModel activity) => new {
        activity.Id,
        activity.DisplayName,
        activity.Type,
        activity.Depth,
        activity.Line,
        activity.Annotation,
        children = activity.Children.Select(ToActivityNode).ToList()
    };
}
