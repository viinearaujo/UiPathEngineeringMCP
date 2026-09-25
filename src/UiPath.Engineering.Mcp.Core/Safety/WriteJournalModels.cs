namespace UiPath.Engineering.Mcp.Core.Safety;

/// <summary>
/// Ambient write context so <see cref="IProjectWriteJournal"/> can record the MCP tool name
/// (and optional checkpoint) without every write site passing it explicitly.
/// </summary>
public static class WriteJournalContext {
    private static readonly AsyncLocal<string?> ToolName = new();
    private static readonly AsyncLocal<string?> CheckpointId = new();

    public static string? CurrentToolName => ToolName.Value;
    public static string? CurrentCheckpointId => CheckpointId.Value;

    public static IDisposable Begin(string toolName, string? checkpointId = null) {
        var previousTool = ToolName.Value;
        var previousCheckpoint = CheckpointId.Value;
        ToolName.Value = toolName;
        if (checkpointId is not null) {
            CheckpointId.Value = checkpointId;
        }

        return new Restore(previousTool, previousCheckpoint);
    }

    public static void SetCheckpoint(string? checkpointId) => CheckpointId.Value = checkpointId;

    private sealed class Restore(string? tool, string? checkpoint) : IDisposable {
        public void Dispose() {
            ToolName.Value = tool;
            CheckpointId.Value = checkpoint;
        }
    }
}

public sealed class JournalEntry {
    public DateTimeOffset Utc { get; init; }
    public string Tool { get; init; } = string.Empty;
    public string RelativePath { get; init; } = string.Empty;
    public string? CheckpointId { get; init; }
}

public interface IProjectWriteJournal {
    void RecordWrite(string absolutePath);
    void SetActiveCheckpoint(string projectPath, string checkpointId);
    string? GetActiveCheckpoint(string projectPath);
    IReadOnlyList<JournalEntry> ReadEntries(string projectPath);
}
