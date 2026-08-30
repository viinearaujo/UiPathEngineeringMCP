using System.ComponentModel;
using System.Diagnostics;
using ModelContextProtocol.Server;
using UiPath.Engineering.Mcp.Core.Abstractions;
using UiPath.Engineering.Mcp.Core.Docs;
using UiPath.Engineering.Mcp.Core.Models;
using UiPath.Engineering.Mcp.Core.Parsing;

namespace UiPath.Engineering.Mcp.Tools;

[McpServerToolType]
public sealed class SyncProjectContextTool {
    private readonly IFilesystemProvider _filesystem;
    private readonly IProjectModelBuilder _modelBuilder;
    private readonly ProjectContextRenderer _renderer;

    public SyncProjectContextTool(
        IFilesystemProvider filesystem,
        IProjectModelBuilder modelBuilder,
        ProjectContextRenderer renderer) {
        _filesystem = filesystem;
        _modelBuilder = modelBuilder;
        _renderer = renderer;
    }

    [McpServerTool(UseStructuredContent = true), Description("Regenerates generated project memory: AGENTS.md (marker-spliced block) and .claude/rules/project-context.md from the current project model.")]
    public async Task<ToolResult> SyncProjectContext(
        [Description("Absolute path to the UiPath project directory (must contain project.json).")] string projectPath,
        CancellationToken cancellationToken = default) {

        var sw = Stopwatch.StartNew();

        if (ToolResults.GuardProject(_filesystem, projectPath, sw) is { } guardFailure) {
            return guardFailure;
        }

        UiPathProjectModel model;
        try {
            model = await _modelBuilder.BuildAsync(projectPath, cancellationToken);
        } catch (Exception ex) {
            return ToolResults.FromException(ex, "Project analysis failed.", sw);
        }

        _renderer.Sync(projectPath, model);
        var (cs, xaml, deps) = ProjectContextRenderer.Counts(model);
        return ToolResults.Ok("Generated project context updated.", new {
            agentsMd = ProjectDocsPaths.AgentsMd(projectPath),
            projectContext = ProjectDocsPaths.ProjectContext(projectPath),
            counts = new { cs, xaml, deps }
        }, sw);
    }
}
