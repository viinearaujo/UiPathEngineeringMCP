using System.Collections.Concurrent;
using System.Text.Json;

namespace UiPath.Engineering.Mcp.Core.Safety;

/// <summary>
/// Append-only write journal at <c>{project}/.mcp/journal.jsonl</c>. Writes under
/// <c>.mcp/</c> are ignored so journal/checkpoint I/O cannot recurse.
/// </summary>
public sealed class ProjectWriteJournal : IProjectWriteJournal {
    private static readonly JsonSerializerOptions JsonOptions = new() {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly ConcurrentDictionary<string, string> _activeCheckpoints =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly object _appendGate = new();

    public void SetActiveCheckpoint(string projectPath, string checkpointId) {
        var root = Path.GetFullPath(projectPath);
        _activeCheckpoints[root] = checkpointId;
        WriteJournalContext.SetCheckpoint(checkpointId);
    }

    public string? GetActiveCheckpoint(string projectPath) {
        var root = Path.GetFullPath(projectPath);
        return _activeCheckpoints.TryGetValue(root, out var id) ? id : null;
    }

    public void RecordWrite(string absolutePath) {
        if (string.IsNullOrWhiteSpace(absolutePath)) {
            return;
        }

        string full;
        try {
            full = Path.GetFullPath(absolutePath);
        } catch {
            return;
        }

        if (IsUnderMcp(full)) {
            return;
        }

        var projectRoot = FindProjectRoot(full);
        if (projectRoot is null) {
            return;
        }

        var relative = Path.GetRelativePath(projectRoot, full).Replace('\\', '/');
        var tool = WriteJournalContext.CurrentToolName ?? "unknown";
        var checkpointId = WriteJournalContext.CurrentCheckpointId
            ?? GetActiveCheckpoint(projectRoot);

        var entry = new JournalEntry {
            Utc = DateTimeOffset.UtcNow,
            Tool = tool,
            RelativePath = relative,
            CheckpointId = checkpointId
        };

        var line = JsonSerializer.Serialize(entry, JsonOptions);
        var journalPath = JournalPath(projectRoot);
        lock (_appendGate) {
            Directory.CreateDirectory(Path.GetDirectoryName(journalPath)!);
            File.AppendAllText(journalPath, line + Environment.NewLine);
        }
    }

    public IReadOnlyList<JournalEntry> ReadEntries(string projectPath) {
        var root = Path.GetFullPath(projectPath);
        var path = JournalPath(root);
        if (!File.Exists(path)) {
            return [];
        }

        var entries = new List<JournalEntry>();
        foreach (var line in File.ReadLines(path)) {
            if (string.IsNullOrWhiteSpace(line)) {
                continue;
            }

            try {
                var entry = JsonSerializer.Deserialize<JournalEntry>(line, JsonOptions);
                if (entry is not null) {
                    entries.Add(entry);
                }
            } catch (JsonException) {
                // Skip corrupt lines.
            }
        }

        return entries;
    }

    internal static string JournalPath(string projectRoot) =>
        Path.Combine(projectRoot, ".mcp", "journal.jsonl");

    internal static bool IsUnderMcp(string absolutePath) {
        var normalized = absolutePath.Replace('\\', '/');
        return normalized.Contains("/.mcp/", StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith("/.mcp", StringComparison.OrdinalIgnoreCase);
    }

    private static string? FindProjectRoot(string absoluteFilePath) {
        var dir = Path.GetDirectoryName(absoluteFilePath);
        while (!string.IsNullOrEmpty(dir)) {
            if (File.Exists(Path.Combine(dir, "project.json"))) {
                return dir;
            }

            dir = Path.GetDirectoryName(dir);
        }

        return null;
    }
}
