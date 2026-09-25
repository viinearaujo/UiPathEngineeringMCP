using System.Text.Json;
using System.Text.Json.Serialization;

namespace UiPath.Engineering.Mcp.AgentEvals;

internal sealed class AgentTaskDefinition {
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("prompt")]
    public string Prompt { get; set; } = "";

    [JsonPropertyName("fixture")]
    public string Fixture { get; set; } = "";

    [JsonPropertyName("assertions")]
    public List<AgentAssertion> Assertions { get; set; } = [];
}

internal sealed class AgentAssertion {
    /// <summary>fileExists | fileContains | planTaskStatus</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("path")]
    public string? Path { get; set; }

    [JsonPropertyName("snippet")]
    public string? Snippet { get; set; }

    [JsonPropertyName("taskId")]
    public string? TaskId { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }
}

internal sealed class AgentTaskResult {
    public required string Id { get; init; }
    public bool Passed { get; init; }
    public string Detail { get; init; } = "";
    public int ToolRounds { get; init; }
}

internal static class AgentTaskLoader {
    private static readonly JsonSerializerOptions JsonOptions = new() {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static string FindRepoRoot() {
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() }) {
            var dir = new DirectoryInfo(start);
            while (dir is not null) {
                var sln = Path.Combine(dir.FullName, "UiPath.Engineering.Mcp.sln");
                var tasks = Path.Combine(dir.FullName, "evals", "agent", "tasks");
                if (File.Exists(sln) && Directory.Exists(tasks)) {
                    return dir.FullName;
                }

                dir = dir.Parent;
            }
        }

        throw new InvalidOperationException(
            "Could not locate repo root (UiPath.Engineering.Mcp.sln + evals/agent/tasks).");
    }

    public static IReadOnlyList<AgentTaskDefinition> LoadAll(string repoRoot) {
        var tasksDir = Path.Combine(repoRoot, "evals", "agent", "tasks");
        var files = Directory.GetFiles(tasksDir, "*.json", SearchOption.TopDirectoryOnly)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (files.Length == 0) {
            throw new InvalidOperationException($"No task JSON files under {tasksDir}.");
        }

        var list = new List<AgentTaskDefinition>(files.Length);
        foreach (var file in files) {
            var json = File.ReadAllText(file);
            var task = JsonSerializer.Deserialize<AgentTaskDefinition>(json, JsonOptions)
                ?? throw new InvalidOperationException($"Failed to deserialize {file}.");
            if (string.IsNullOrWhiteSpace(task.Id)) {
                task.Id = Path.GetFileNameWithoutExtension(file);
            }

            list.Add(task);
        }

        return list;
    }

    public static string MaterializeFixture(string repoRoot, string fixtureName, string targetProjectDir) {
        var source = Path.Combine(repoRoot, "evals", "agent", "fixtures", fixtureName);
        if (!Directory.Exists(source)) {
            throw new DirectoryNotFoundException($"Fixture not found: {source}");
        }

        CopyDirectory(source, targetProjectDir);
        return targetProjectDir;
    }

    private static void CopyDirectory(string sourceDir, string destDir) {
        Directory.CreateDirectory(destDir);
        foreach (var file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories)) {
            var rel = Path.GetRelativePath(sourceDir, file);
            var dest = Path.Combine(destDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, overwrite: true);
        }
    }
}
