using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using UiPath.Engineering.Mcp.Core.Abstractions;
using UiPath.Engineering.Mcp.Core.Canvas;
using UiPath.Engineering.Mcp.Core.Configuration;
using UiPath.Engineering.Mcp.Core.Models;
using UiPath.Engineering.Mcp.Core.Parsing;

namespace UiPath.Engineering.Mcp.Tools;

[McpServerToolType]
public sealed class GenerateDocumentationTool {
    private readonly IFilesystemProvider _filesystem;
    private readonly IProjectModelBuilder _modelBuilder;
    private readonly string _mcpVersion;

    public GenerateDocumentationTool(
        IFilesystemProvider filesystem,
        IProjectModelBuilder modelBuilder,
        IOptions<UiPath.Engineering.Mcp.Core.Configuration.McpServerOptions>? serverOptions = null) {
        _filesystem = filesystem;
        _modelBuilder = modelBuilder;
        _mcpVersion = serverOptions?.Value.Version ?? new UiPath.Engineering.Mcp.Core.Configuration.McpServerOptions().Version;
    }

    [McpServerTool(
        UseStructuredContent = true,
        Title = "Generate Documentation",
        ReadOnly = false,
        Destructive = false,
        Idempotent = true),
     Description("Leave-off. Omit format for documentation data. format \"canvasSnapshot\" writes <project>/.canvas/snapshot.json and replaces prose already stored. Enable on ToolSurface=All for the skeleton pass. Next: update_canvas_snapshot.")]
    public async Task<ToolResult> GenerateDocumentation(
        [Description("Absolute path to the UiPath project directory.")] string projectPath,
        [Description("Omit, or \"default\", for the documentation payload. \"canvasSnapshot\" writes the fact skeleton.")] string? format = null,
        CancellationToken cancellationToken = default) {
        var sw = Stopwatch.StartNew();

        if (ToolResults.GuardAllowedPath(_filesystem, projectPath, sw) is { } guardFailure) {
            return guardFailure;
        }

        if (string.Equals(format, "canvasSnapshot", StringComparison.Ordinal)) {
            return await WriteCanvasSnapshot(projectPath, sw, cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(format)
            && !string.Equals(format, "default", StringComparison.OrdinalIgnoreCase)) {
            return ToolResults.Failure(
                "format must be omitted or canvasSnapshot.",
                "format must be omitted or canvasSnapshot.",
                sw);
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

    private async Task<ToolResult> WriteCanvasSnapshot(string projectPath, Stopwatch sw, CancellationToken cancellationToken) {
        var model = await _modelBuilder.BuildAsync(projectPath, cancellationToken);
        var draft = CanvasSnapshotSkeleton.Build(
            model,
            projectPath,
            _mcpVersion,
            _filesystem.ReadAllBytes,
            DateTime.UtcNow);
        if (draft.Error is not null) {
            return ToolResults.Failure(draft.Error, draft.Error, sw);
        }

        var path = CanvasSnapshotPath.ForProject(projectPath);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) {
            _filesystem.CreateDirectory(directory);
        }

        _filesystem.WriteAllText(path, draft.Json);
        return ToolResults.Ok(
            $"Wrote {path} ({draft.NodeCount} nodes, {draft.EdgeCount} edges, generatedAt {draft.GeneratedAt}).",
            new {
                path,
                nodeCount = draft.NodeCount,
                edgeCount = draft.EdgeCount,
                generatedAt = draft.GeneratedAt
            },
            sw);
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
