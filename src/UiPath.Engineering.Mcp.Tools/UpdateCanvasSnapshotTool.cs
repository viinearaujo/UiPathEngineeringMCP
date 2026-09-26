using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using ModelContextProtocol.Server;
using UiPath.Engineering.Mcp.Core.Abstractions;
using UiPath.Engineering.Mcp.Core.Canvas;
using UiPath.Engineering.Mcp.Core.Models;

namespace UiPath.Engineering.Mcp.Tools;

[McpServerToolType]
public sealed class UpdateCanvasSnapshotTool {
    private readonly IFilesystemProvider _filesystem;

    public UpdateCanvasSnapshotTool(IFilesystemProvider filesystem) => _filesystem = filesystem;

    [McpServerTool(
        UseStructuredContent = true,
        Title = "Update Canvas Snapshot",
        ReadOnly = false,
        Destructive = false,
        Idempotent = true),
     Description("Status or prose patch for <project>/.canvas/snapshot.json. Omit overview and nodes to report remaining work. Patches only project.overview and node explanation/decisions.")]
    public Task<ToolResult> UpdateCanvasSnapshot(
        [Description("Absolute path to the UiPath project directory.")] string projectPath,
        [Description("When set, replaces project.overview. Must be a string. Omit to leave it unchanged.")] JsonElement? overview = null,
        [Description("Nodes to patch. Each object requires id, explanation, and decisions (string array). Omit or pass [] for a status call.")] JsonElement? nodes = null,
        CancellationToken cancellationToken = default) {
        var sw = Stopwatch.StartNew();
        _ = cancellationToken;
        if (ToolResults.GuardAllowedPath(_filesystem, projectPath, sw) is { } guardFailure) {
            return Task.FromResult(guardFailure);
        }

        var path = CanvasSnapshotPath.ForProject(projectPath);
        if (!_filesystem.FileExists(path)) {
            return Task.FromResult(ToolResults.Failure(
                "snapshot.json does not exist",
                "snapshot.json does not exist",
                sw));
        }

        var json = _filesystem.ReadAllText(path);
        if (!CanvasSnapshotDocument.TryRead(json, out var root, out var error) || root is null) {
            return Task.FromResult(ToolResults.Failure(error ?? "invalid JSON", error ?? "invalid JSON", sw));
        }

        var validation = CanvasSnapshotDocument.ValidatePatch(
            root,
            overview,
            nodes,
            out var overviewText,
            out var setOverview,
            out var patches);
        if (validation is not null) {
            return Task.FromResult(ToolResults.Failure(validation, validation, sw));
        }

        var writing = setOverview || patches.Count > 0;
        if (writing) {
            CanvasSnapshotDocument.Apply(root, setOverview, overviewText, patches);
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) {
                _filesystem.CreateDirectory(directory);
            }

            // Prose patch. Do not call ProjectFilePolicy.ValidateMutatingFile.
            _filesystem.WriteAllText(path, CanvasSnapshotDocument.ToIndented(root));
        }

        var status = CanvasSnapshotDocument.Status(root, writing);
        var summary = writing
            ? $"Updated canvas snapshot: {status.ExplainedCount}/{status.TotalCount} explanations."
            : $"Canvas snapshot status: {status.ExplainedCount}/{status.TotalCount} explanations, {status.RemainingCount} remaining.";
        return Task.FromResult(ToolResults.Ok(summary, Payload(status), sw));
    }

    internal static object Payload(CanvasSnapshotStatus status) => new {
        written = status.Written,
        overviewWritten = status.OverviewWritten,
        explainedCount = status.ExplainedCount,
        totalCount = status.TotalCount,
        remainingCount = status.RemainingCount,
        nextNodeIds = status.NextNodeIds
    };
}
