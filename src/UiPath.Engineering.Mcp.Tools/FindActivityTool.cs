using System.ComponentModel;
using System.Diagnostics;
using ModelContextProtocol.Server;
using UiPath.Engineering.Mcp.Core.Abstractions;
using UiPath.Engineering.Mcp.Core.Models;
using UiPath.Engineering.Mcp.Core.Parsing;

namespace UiPath.Engineering.Mcp.Tools;

[McpServerToolType]
public sealed class FindActivityTool {
    private readonly IFilesystemProvider _filesystem;
    private readonly IProjectModelBuilder _modelBuilder;

    public FindActivityTool(IFilesystemProvider filesystem, IProjectModelBuilder modelBuilder) {
        _filesystem = filesystem;
        _modelBuilder = modelBuilder;
    }

    [McpServerTool, Description("Finds activities inside UiPath .xaml workflows and returns their stable activity IDs, line numbers, and ancestor chain. Filter by workflowFile, DisplayName substring (query), exact activity type, or exact activity ID. Pass the returned id to edit_workflow_activity / insert_activities as activityId. IDs are per-parse-snapshot: after a structural edit, re-run find_activity before using IDs captured earlier.")]
    public async Task<ToolResult> FindActivity(
        [Description("Absolute path to the UiPath project directory (must contain project.json).")] string projectPath,
        [Description("Optional workflow file name (with or without .xaml) to limit the search to one workflow.")] string? workflowFile = null,
        [Description("Optional DisplayName substring, case-insensitive.")] string? query = null,
        [Description("Optional exact activity type, e.g. 'LogMessage'.")] string? activityType = null,
        [Description("Optional exact activity ID, e.g. 'sequence.1/if.1'. When supplied, query/activityType are ignored, but workflowFile still narrows the lookup — pass it together with activityId, since IDs are per-workflow paths that collide across workflows (e.g. 'sequence.1' matches the root sequence of every workflow).")] string? activityId = null,
        CancellationToken cancellationToken = default) {
        var sw = Stopwatch.StartNew();

        if (ToolResults.GuardProject(_filesystem, projectPath, sw) is { } guardFailure) {
            return guardFailure;
        }

        try {
            var model = await _modelBuilder.BuildAsync(projectPath, cancellationToken);

            IEnumerable<WorkflowModel> workflows = model.Workflows.Where(w => !w.HasParseError);
            if (workflowFile is not null) {
                var requestedName = Path.GetFileName(workflowFile);
                if (!requestedName.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)) {
                    requestedName += ".xaml";
                }
                workflows = workflows.Where(w =>
                    string.Equals(w.FileName, requestedName, StringComparison.OrdinalIgnoreCase));
            }

            var matches = new List<object>();
            foreach (var workflow in workflows) {
                var byId = workflow.Activities.ToDictionary(a => a.Id, StringComparer.Ordinal);
                foreach (var activity in workflow.Activities) {
                    if (activityId is not null) {
                        if (!string.Equals(activity.Id, activityId, StringComparison.Ordinal)) {
                            continue;
                        }
                    } else {
                        if (query is not null
                            && !activity.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)) {
                            continue;
                        }
                        if (activityType is not null
                            && !string.Equals(activity.Type, activityType, StringComparison.OrdinalIgnoreCase)) {
                            continue;
                        }
                    }

                    matches.Add(new {
                        id = activity.Id,
                        displayName = activity.DisplayName,
                        type = activity.Type,
                        workflowFile = workflow.FileName,
                        line = activity.Line,
                        parentId = activity.ParentId,
                        depth = activity.Depth,
                        ancestors = AncestorsOf(activity, byId)
                    });
                }
            }

            var note = matches.Count == 0
                ? "No activities matched the filters. Broaden the query or check the workflowFile name."
                : "Activity IDs are per-parse-snapshot; re-run find_activity after structural edits.";
            return ToolResults.Ok(
                matches.Count == 1 ? "1 activity matched." : $"{matches.Count} activities matched.",
                new { matches, note }, sw);
        } catch (Exception ex) {
            return ToolResults.FromException(ex, "Activity search failed.", sw);
        }
    }

    private static List<object> AncestorsOf(ActivityModel activity, Dictionary<string, ActivityModel> byId) {
        var chain = new List<object>();
        var current = activity.ParentId;
        while (current is not null && byId.TryGetValue(current, out var parent)) {
            chain.Add(new { id = parent.Id, displayName = parent.DisplayName });
            current = parent.ParentId;
        }
        chain.Reverse(); // root-first
        return chain;
    }
}
