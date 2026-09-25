using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace UiPath.Engineering.Mcp.Core.Safety;

public sealed class CheckpointManifest {
    public string CheckpointId { get; init; } = string.Empty;
    public DateTimeOffset CreatedUtc { get; init; }
    public string? Head { get; init; }
    public bool UsedGit { get; init; }
    public string? Diff { get; init; }
    public List<string> Untracked { get; init; } = [];
    public List<string> Files { get; init; } = [];
}

public sealed class CheckpointCreateResult {
    public required string CheckpointId { get; init; }
    public required CheckpointManifest Manifest { get; init; }
    public string Mode { get; init; } = "files";
}

public sealed class CheckpointDiffResult {
    public required string CheckpointId { get; init; }
    public required string Diff { get; init; }
    public bool Truncated { get; init; }
}

/// <summary>
/// Snapshots writable project files under <c>.mcp/checkpoints/</c> without moving the user's
/// git HEAD. Prefers recording git HEAD/diff metadata when git is available; always copies
/// file contents for safe restore.
/// </summary>
public sealed class ProjectCheckpointService {
    public const int MaxDiffBytes = 100 * 1024;

    private static readonly string[] SnapshotExtensions = [".xaml", ".cs", ".md", ".json", ".txt"];
    private static readonly JsonSerializerOptions JsonOptions = new() {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly IProjectWriteJournal _journal;

    public ProjectCheckpointService(IProjectWriteJournal journal) => _journal = journal;

    public CheckpointCreateResult Create(string projectPath) {
        var root = Path.GetFullPath(projectPath);
        var id = Guid.NewGuid().ToString("N")[..12];
        var checkpointDir = CheckpointDir(root, id);
        Directory.CreateDirectory(checkpointDir);

        string? head = null;
        string? diff = null;
        var untracked = new List<string>();
        var usedGit = TryReadGitState(root, out head, out diff, out untracked);

        var files = CollectWritableFiles(root);
        if (usedGit) {
            // Prefer dirty + untracked when git works; still snapshot those paths' contents.
            var dirty = ParseDirtyPaths(diff, untracked);
            if (dirty.Count > 0) {
                files = files.Where(f => dirty.Contains(NormalizeRelative(f))).ToList();
                // Always keep the listed dirty set even if CollectWritableFiles missed one.
                foreach (var rel in dirty) {
                    if (!files.Contains(rel, StringComparer.OrdinalIgnoreCase)
                        && IsSnapshotCandidate(rel)
                        && File.Exists(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)))) {
                        files.Add(rel);
                    }
                }
            }
        }

        var copied = new List<string>();
        foreach (var relative in files) {
            var source = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(source)) {
                continue;
            }

            var dest = Path.Combine(checkpointDir, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(source, dest, overwrite: true);
            copied.Add(NormalizeRelative(relative));
        }

        var manifest = new CheckpointManifest {
            CheckpointId = id,
            CreatedUtc = DateTimeOffset.UtcNow,
            Head = head,
            UsedGit = usedGit,
            Diff = Truncate(diff, MaxDiffBytes),
            Untracked = untracked,
            Files = copied
        };

        File.WriteAllText(ManifestPath(root, id), JsonSerializer.Serialize(manifest, JsonOptions));
        _journal.SetActiveCheckpoint(root, id);
        return new CheckpointCreateResult {
            CheckpointId = id,
            Manifest = manifest,
            Mode = usedGit ? "git-metadata+files" : "files"
        };
    }

    public CheckpointDiffResult GetChanges(string projectPath, string? checkpointId) {
        var root = Path.GetFullPath(projectPath);
        var id = checkpointId ?? _journal.GetActiveCheckpoint(root)
            ?? throw new InvalidOperationException("No checkpointId was supplied and no active checkpoint exists.");
        var manifest = LoadManifest(root, id);
        var checkpointDir = CheckpointDir(root, id);

        var sb = new StringBuilder();
        var truncated = false;
        var currentFiles = CollectWritableFiles(root).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var relative in manifest.Files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase)) {
            if (sb.Length >= MaxDiffBytes) {
                truncated = true;
                break;
            }

            var snapPath = Path.Combine(checkpointDir, relative.Replace('/', Path.DirectorySeparatorChar));
            var livePath = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            var snapText = File.Exists(snapPath) ? File.ReadAllText(snapPath) : string.Empty;
            var liveText = File.Exists(livePath) ? File.ReadAllText(livePath) : string.Empty;
            if (string.Equals(snapText, liveText, StringComparison.Ordinal)) {
                currentFiles.Remove(relative);
                continue;
            }

            AppendUnified(sb, relative, snapText, liveText);
            currentFiles.Remove(relative);
            if (sb.Length >= MaxDiffBytes) {
                truncated = true;
                break;
            }
        }

        foreach (var relative in currentFiles.OrderBy(f => f, StringComparer.OrdinalIgnoreCase)) {
            if (manifest.Files.Contains(relative, StringComparer.OrdinalIgnoreCase)) {
                continue;
            }

            if (sb.Length >= MaxDiffBytes) {
                truncated = true;
                break;
            }

            var livePath = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(livePath)) {
                continue;
            }

            AppendUnified(sb, relative, string.Empty, File.ReadAllText(livePath));
        }

        var text = sb.ToString();
        if (text.Length > MaxDiffBytes) {
            text = text[..MaxDiffBytes];
            truncated = true;
        }

        return new CheckpointDiffResult {
            CheckpointId = id,
            Diff = text,
            Truncated = truncated
        };
    }

    public int Revert(string projectPath, string checkpointId) {
        var root = Path.GetFullPath(projectPath);
        var manifest = LoadManifest(root, checkpointId);
        var checkpointDir = CheckpointDir(root, checkpointId);
        var restored = 0;

        foreach (var relative in manifest.Files) {
            var snapPath = Path.Combine(checkpointDir, relative.Replace('/', Path.DirectorySeparatorChar));
            var livePath = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(snapPath)) {
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(livePath)!);
            File.Copy(snapPath, livePath, overwrite: true);
            restored++;
        }

        var snapSet = manifest.Files.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in _journal.ReadEntries(root)) {
            if (!string.Equals(entry.CheckpointId, checkpointId, StringComparison.Ordinal)) {
                continue;
            }

            if (snapSet.Contains(entry.RelativePath)) {
                continue;
            }

            var created = Path.Combine(root, entry.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(created) && !IsUnderMcp(created)) {
                File.Delete(created);
            }
        }

        return restored;
    }

    public CheckpointManifest LoadManifest(string projectPath, string checkpointId) {
        var path = ManifestPath(Path.GetFullPath(projectPath), checkpointId);
        if (!File.Exists(path)) {
            throw new FileNotFoundException($"Checkpoint '{checkpointId}' was not found.", path);
        }

        return JsonSerializer.Deserialize<CheckpointManifest>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidOperationException($"Checkpoint '{checkpointId}' manifest is invalid.");
    }

    private static string CheckpointDir(string root, string id) =>
        Path.Combine(root, ".mcp", "checkpoints", id);

    private static string ManifestPath(string root, string id) =>
        Path.Combine(root, ".mcp", "checkpoints", id + ".json");

    private static bool IsUnderMcp(string absolutePath) =>
        ProjectWriteJournal.IsUnderMcp(absolutePath);

    private static List<string> CollectWritableFiles(string root) {
        var results = new List<string>();
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) {
            if (IsUnderMcp(file)) {
                continue;
            }

            var name = Path.GetFileName(file);
            if (name.Equals("project.json", StringComparison.OrdinalIgnoreCase)
                || IsSnapshotCandidate(Path.GetRelativePath(root, file))) {
                results.Add(NormalizeRelative(Path.GetRelativePath(root, file)));
            }
        }

        return results;
    }

    private static bool IsSnapshotCandidate(string relativePath) {
        var normalized = NormalizeRelative(relativePath);
        if (normalized.Equals("project.json", StringComparison.OrdinalIgnoreCase)) {
            return true;
        }

        var ext = Path.GetExtension(normalized);
        if (!SnapshotExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase)) {
            return false;
        }

        // .md/.json/.txt outside docs are still writable via manage_project_content; include them.
        return true;
    }

    private static string NormalizeRelative(string relativePath) =>
        relativePath.Replace('\\', '/').TrimStart('/');

    private static HashSet<string> ParseDirtyPaths(string? diff, List<string> untracked) {
        var set = new HashSet<string>(untracked.Select(NormalizeRelative), StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(diff)) {
            return set;
        }

        foreach (var line in diff.Split('\n')) {
            if (line.StartsWith("+++ b/", StringComparison.Ordinal)
                || line.StartsWith("--- a/", StringComparison.Ordinal)) {
                var path = line[6..].Trim();
                if (path is not "/dev/null") {
                    set.Add(NormalizeRelative(path));
                }
            }
        }

        return set;
    }

    private static void AppendUnified(StringBuilder sb, string relative, string before, string after) {
        sb.Append("--- a/").Append(relative).Append('\n');
        sb.Append("+++ b/").Append(relative).Append('\n');
        var beforeLines = before.Replace("\r\n", "\n").Split('\n');
        var afterLines = after.Replace("\r\n", "\n").Split('\n');
        // Compact line-oriented dump (not a full Myers diff) — enough for agent review.
        foreach (var line in beforeLines) {
            if (sb.Length >= MaxDiffBytes) {
                return;
            }

            sb.Append('-').Append(line).Append('\n');
        }

        foreach (var line in afterLines) {
            if (sb.Length >= MaxDiffBytes) {
                return;
            }

            sb.Append('+').Append(line).Append('\n');
        }
    }

    private static string? Truncate(string? text, int max) {
        if (text is null) {
            return null;
        }

        return text.Length <= max ? text : text[..max];
    }

    private static bool TryReadGitState(
        string root,
        out string? head,
        out string? diff,
        out List<string> untracked) {
        head = null;
        diff = null;
        untracked = [];

        if (!RunGit(root, "rev-parse HEAD", out var headOut) || string.IsNullOrWhiteSpace(headOut)) {
            return false;
        }

        head = headOut.Trim();
        if (RunGit(root, "diff HEAD", out var diffOut)) {
            diff = diffOut;
        }

        if (RunGit(root, "ls-files --others --exclude-standard", out var untrackedOut)) {
            untracked = untrackedOut
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Select(NormalizeRelative)
                .ToList();
        }

        return true;
    }

    private static bool RunGit(string root, string arguments, out string stdout) {
        stdout = string.Empty;
        try {
            using var process = Process.Start(new ProcessStartInfo {
                FileName = "git",
                Arguments = $"-C \"{root}\" {arguments}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (process is null) {
                return false;
            }

            stdout = process.StandardOutput.ReadToEnd();
            process.WaitForExit(15_000);
            return process.ExitCode == 0;
        } catch {
            return false;
        }
    }
}
