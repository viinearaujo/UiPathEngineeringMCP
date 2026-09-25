using System.ComponentModel;
using System.Diagnostics;
using ModelContextProtocol.Server;
using UiPath.Engineering.Mcp.Core.Abstractions;
using UiPath.Engineering.Mcp.Core.Models;
using UiPath.Engineering.Mcp.Core.Safety;

namespace UiPath.Engineering.Mcp.Tools;

[McpServerToolType]
public sealed class CheckpointTool {
    private readonly IFilesystemProvider _filesystem;
    private readonly ProjectCheckpointService _checkpoints;

    public CheckpointTool(IFilesystemProvider filesystem, ProjectCheckpointService checkpoints) {
        _filesystem = filesystem;
        _checkpoints = checkpoints;
    }

    [McpServerTool(
        UseStructuredContent = true,
        Title = "Checkpoint",
        ReadOnly = false,
        Destructive = true,
        Idempotent = false),
     Description("Snapshots writable project files under .mcp/checkpoints (git HEAD/diff metadata when available; never moves HEAD or pushes). Returns checkpointId. Next: get_changes or revert_changes.")]
    public ToolResult Checkpoint(
        [Description("Absolute path to the UiPath project directory.")] string projectPath) {
        var sw = Stopwatch.StartNew();
        if (ToolResults.GuardProject(_filesystem, projectPath, sw) is { } guardFailure) {
            return guardFailure;
        }

        try {
            var created = _checkpoints.Create(projectPath);
            return ToolResults.Ok(
                $"Checkpoint '{created.CheckpointId}' created ({created.Mode}).",
                new {
                    checkpointId = created.CheckpointId,
                    mode = created.Mode,
                    fileCount = created.Manifest.Files.Count,
                    usedGit = created.Manifest.UsedGit,
                    head = created.Manifest.Head
                },
                sw);
        } catch (Exception ex) {
            return ToolResults.Failure($"Checkpoint failed: {ex.Message}", sw);
        }
    }
}

[McpServerToolType]
public sealed class GetChangesTool {
    private readonly IFilesystemProvider _filesystem;
    private readonly ProjectCheckpointService _checkpoints;
    private readonly IProjectWriteJournal _journal;

    public GetChangesTool(
        IFilesystemProvider filesystem,
        ProjectCheckpointService checkpoints,
        IProjectWriteJournal journal) {
        _filesystem = filesystem;
        _checkpoints = checkpoints;
        _journal = journal;
    }

    [McpServerTool(
        UseStructuredContent = true,
        Title = "Get Changes",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true),
     Description("Unified diff of writable files since checkpointId (or the active checkpoint). Caps at 100 KB (truncated=true past that). Next: revert_changes or continue editing.")]
    public ToolResult GetChanges(
        [Description("Absolute path to the UiPath project directory.")] string projectPath,
        [Description("Optional checkpoint id from checkpoint. Defaults to the active checkpoint.")] string? checkpointId = null) {
        var sw = Stopwatch.StartNew();
        if (ToolResults.GuardProject(_filesystem, projectPath, sw) is { } guardFailure) {
            return guardFailure;
        }

        try {
            var id = string.IsNullOrWhiteSpace(checkpointId)
                ? _journal.GetActiveCheckpoint(projectPath)
                : checkpointId.Trim();
            if (string.IsNullOrWhiteSpace(id)) {
                return ToolResults.Failure("checkpointId is required when no active checkpoint exists.", sw);
            }

            var diff = _checkpoints.GetChanges(projectPath, id);
            return ToolResults.Ok(
                diff.Truncated
                    ? $"Diff for '{diff.CheckpointId}' truncated at 100 KB."
                    : $"Diff for '{diff.CheckpointId}'.",
                new {
                    checkpointId = diff.CheckpointId,
                    diff = diff.Diff,
                    truncated = diff.Truncated
                },
                sw);
        } catch (Exception ex) {
            return ToolResults.Failure($"get_changes failed: {ex.Message}", sw);
        }
    }
}

[McpServerToolType]
public sealed class RevertChangesTool {
    private readonly IFilesystemProvider _filesystem;
    private readonly ProjectCheckpointService _checkpoints;

    public RevertChangesTool(IFilesystemProvider filesystem, ProjectCheckpointService checkpoints) {
        _filesystem = filesystem;
        _checkpoints = checkpoints;
    }

    [McpServerTool(
        UseStructuredContent = true,
        Title = "Revert Changes",
        ReadOnly = false,
        Destructive = true,
        Idempotent = false),
     Description("Restores snapshotted files for checkpointId and deletes journaled files created after it. Does not git reset --hard. Next: check_work.")]
    public ToolResult RevertChanges(
        [Description("Absolute path to the UiPath project directory.")] string projectPath,
        [Description("Checkpoint id from checkpoint.")] string checkpointId) {
        var sw = Stopwatch.StartNew();
        if (ToolResults.GuardProject(_filesystem, projectPath, sw) is { } guardFailure) {
            return guardFailure;
        }

        if (string.IsNullOrWhiteSpace(checkpointId)) {
            return ToolResults.Failure("checkpointId is required.", sw);
        }

        try {
            var restored = _checkpoints.Revert(projectPath, checkpointId.Trim());
            return ToolResults.Ok(
                $"Reverted checkpoint '{checkpointId}' ({restored} file(s) restored).",
                new { checkpointId = checkpointId.Trim(), restoredFiles = restored },
                sw);
        } catch (Exception ex) {
            return ToolResults.Failure($"revert_changes failed: {ex.Message}", sw);
        }
    }
}
