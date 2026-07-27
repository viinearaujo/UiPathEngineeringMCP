using System.ComponentModel;
using System.Diagnostics;
using ModelContextProtocol.Server;
using UiPath.Engineering.Mcp.Core.Abstractions;
using UiPath.Engineering.Mcp.Core.Models;
using UiPath.Engineering.Mcp.Core.Parsing;

namespace UiPath.Engineering.Mcp.Tools;

[McpServerToolType]
public sealed class AnalyzeProjectTool
{
    private readonly IFilesystemProvider _filesystem;
    private readonly IProjectModelBuilder _modelBuilder;

    public AnalyzeProjectTool(IFilesystemProvider filesystem, IProjectModelBuilder modelBuilder)
    {
        _filesystem = filesystem;
        _modelBuilder = modelBuilder;
    }

    [McpServerTool, Description("Analyzes a UiPath project and returns structured metadata, workflows, and dependencies.")]
    public async Task<ToolResult> AnalyzeProject(
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

            return new ToolResult
            {
                Summary = "Project analyzed successfully.",
                Data = model,
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
                Summary = "Project analysis failed.",
                Errors = [ex.Message],
                DurationMs = sw.ElapsedMilliseconds
            };
        }
    }
}
