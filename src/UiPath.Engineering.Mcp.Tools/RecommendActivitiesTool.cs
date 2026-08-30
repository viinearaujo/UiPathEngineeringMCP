using System.ComponentModel;
using System.Diagnostics;
using ModelContextProtocol.Server;
using UiPath.Engineering.Mcp.Core;
using UiPath.Engineering.Mcp.Core.Abstractions;
using UiPath.Engineering.Mcp.Core.Authoring;
using UiPath.Engineering.Mcp.Core.Models;

namespace UiPath.Engineering.Mcp.Tools;

[McpServerToolType]
public sealed class RecommendActivitiesTool {
    private readonly IFilesystemProvider _filesystem;
    private readonly IActivityCatalogResolver _catalogResolver;

    public RecommendActivitiesTool(IFilesystemProvider filesystem, IActivityCatalogResolver catalogResolver) {
        _filesystem = filesystem;
        _catalogResolver = catalogResolver;
    }

    [McpServerTool(UseStructuredContent = true), Description("Recommends up to 5 version-aware activity schemas for a natural-language step against the project's installed packages (uip activities find when available; otherwise the built-in fallback catalog). Call this before validate_activity_spec / build_workflow when the activity type is unknown.")]
    public async Task<ToolResult> RecommendActivities(
        [Description("Natural-language step or activity name, e.g. 'read excel range' or 'Click'.")] string query,
        [Description("Absolute path to the UiPath project directory (must contain project.json).")] string projectPath,
        CancellationToken cancellationToken = default) {

        var sw = Stopwatch.StartNew();

        if (ToolResults.GuardProject(_filesystem, projectPath, sw) is { } guardFailure) {
            return guardFailure;
        }

        if (string.IsNullOrWhiteSpace(query)) {
            return ToolResults.Failure("query is required.",
                [new ToolError(ToolErrorCodes.InvalidArgument,
                    "The 'query' parameter must not be empty.",
                    "Describe the step, e.g. 'read excel range' or 'log message'.")],
                sw);
        }

        try {
            var hits = await _catalogResolver.RecommendAsync(query, projectPath, ActivityCatalogResolver.MaxRecommendations, cancellationToken);
            var catalog = await _catalogResolver.ResolveAsync(projectPath, cancellationToken);
            var summary = hits.Count == 0
                ? $"No catalog activities matched '{query}' (source: {catalog.Source})."
                : $"Recommended {hits.Count} activity schema(s) for '{query}' (source: {catalog.Source}).";
            return ToolResults.Ok(summary, new {
                query,
                source = catalog.Source,
                activities = hits
            }, sw);
        } catch (Exception ex) {
            return ToolResults.FromException(ex, "Activity recommendation failed.", sw);
        }
    }
}
