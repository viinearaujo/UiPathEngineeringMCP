using System.ComponentModel;
using System.Diagnostics;
using ModelContextProtocol.Server;
using UiPath.Engineering.Mcp.Core.Abstractions;
using UiPath.Engineering.Mcp.Core.Models;
using UiPath.Engineering.Mcp.Core.Parsing;

namespace UiPath.Engineering.Mcp.Tools;

[McpServerToolType]
public sealed class GenerateDocumentationTool
{
    private readonly IFilesystemProvider _filesystem;
    private readonly IProjectModelBuilder _modelBuilder;

    public GenerateDocumentationTool(IFilesystemProvider filesystem, IProjectModelBuilder modelBuilder)
    {
        _filesystem = filesystem;
        _modelBuilder = modelBuilder;
    }

    [McpServerTool, Description("Generates structured documentation data for a UiPath project: metadata, per-workflow summaries, dependency graph, and risks.")]
    public async Task<ToolResult> GenerateDocumentation(
        [Description("Absolute path to the UiPath project directory.")] string projectPath,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();

        if (!_filesystem.IsPathAllowed(projectPath))
        {
            return new ToolResult
            {
                Status = "error",
                Summary = "Path not allowed.",
                Errors = ["The requested path is outside the allowed project roots."],
                DurationMs = sw.ElapsedMilliseconds
            };
        }

        try
        {
            var model = await _modelBuilder.BuildAsync(projectPath, cancellationToken);
            var graph = DependencyGraphBuilder.Build(model.Workflows, model.MainWorkflow);

            var data = new
            {
                Project = new
                {
                    model.ProjectName,
                    model.ProjectPath,
                    model.MainWorkflow,
                    model.Description,
                    model.ReadmeSummary,
                    Packages = model.Packages.Select(p => new { p.Id, p.Version }).ToList()
                },
                Workflows = model.Workflows.Select(w => new
                {
                    w.FileName,
                    w.IsMain,
                    ArgumentCount = w.Arguments.Count,
                    ArgumentNames = w.Arguments.Select(a => a.Name).ToList(),
                    VariableCount = w.Variables.Count,
                    ActivityOutline = w.Activities
                        .Where(a => a.Depth <= 1)
                        .Select(a => new { a.DisplayName, a.Type, a.Depth })
                        .ToList(),
                    InvokedWorkflows = w.InvokeWorkflows.Select(i => i.TargetWorkflow).ToList(),
                    LogMessageCount = w.LogMessages.Count,
                    w.HasParseError,
                    w.ParseError
                }).ToList(),
                DependencyGraph = new
                {
                    Edges = graph.Edges.Select(e => new { e.Source, e.Target, e.IsResolved }).ToList(),
                    graph.Cycles,
                    graph.Orphans
                },
                model.Risks
            };

            return new ToolResult
            {
                Summary = $"Documentation data generated for project '{model.ProjectName}' ({model.Workflows.Count} workflows, {model.Risks.Count} risks).",
                Data = data,
                DurationMs = sw.ElapsedMilliseconds
            };
        }
        catch (FileNotFoundException ex)
        {
            return new ToolResult
            {
                Status = "error",
                Summary = "project.json not found.",
                Errors = [ex.Message],
                DurationMs = sw.ElapsedMilliseconds
            };
        }
        catch (System.Text.Json.JsonException ex)
        {
            return new ToolResult
            {
                Status = "error",
                Summary = "project.json could not be parsed.",
                Errors = [$"Invalid JSON in project.json: {ex.Message}"],
                DurationMs = sw.ElapsedMilliseconds
            };
        }
        catch (Exception ex)
        {
            // Never surface a raw exception/stack trace to the MCP client.
            return new ToolResult
            {
                Status = "error",
                Summary = "Documentation generation failed.",
                Errors = [ex.Message],
                DurationMs = sw.ElapsedMilliseconds
            };
        }
    }
}
