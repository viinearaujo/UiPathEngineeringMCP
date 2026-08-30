using System.Text.Json;
using UiPath.Engineering.Mcp.Core.Models;
using UiPath.Engineering.Mcp.Core.Abstractions;

namespace UiPath.Engineering.Mcp.Core.Parsing;

public sealed class ProjectJsonParser {
    private readonly IFilesystemProvider _filesystem;

    public ProjectJsonParser(IFilesystemProvider filesystem) => _filesystem = filesystem;

    public UiPathProjectModel Parse(string projectJsonPath, string projectRoot) {
        var jsonContent = _filesystem.ReadAllText(projectJsonPath);
        using var doc = JsonDocument.Parse(jsonContent);
        var root = doc.RootElement;

        var mainWorkflow = root.TryGetProperty("main", out var main) ? main.GetString() : null;

        var entryPoints = new List<string>();
        if (root.TryGetProperty("entryPoints", out var eps) && eps.ValueKind == JsonValueKind.Array) {
            foreach (var ep in eps.EnumerateArray()) {
                var filePath = ep.ValueKind == JsonValueKind.Object && ep.TryGetProperty("filePath", out var fp)
                    ? fp.GetString()
                    : null;
                if (!string.IsNullOrWhiteSpace(filePath)) {
                    entryPoints.Add(filePath);
                }
            }
        }

        var dependencies = new List<string>();
        var packages = new List<PackageModel>();
        if (root.TryGetProperty("dependencies", out var deps) && deps.ValueKind == JsonValueKind.Object) {
            foreach (var p in deps.EnumerateObject()) {
                var version = p.Value.GetString() ?? "unknown";
                dependencies.Add($"{p.Name} ({version})");
                packages.Add(new PackageModel { Id = p.Name, Version = version });
            }
        }

        return new UiPathProjectModel {
            ProjectPath = projectRoot,
            ProjectJsonPath = projectJsonPath,
            ProjectName = root.TryGetProperty("name", out var name) ? name.GetString() ?? "Unknown" : "Unknown",
            MainWorkflow = mainWorkflow,
            EntryPoints = entryPoints,
            Description = root.TryGetProperty("description", out var desc) ? desc.GetString() : null,
            TargetFramework = root.TryGetProperty("targetFramework", out var tf) ? tf.GetString() : null,
            ExpressionLanguage = root.TryGetProperty("expressionLanguage", out var el) ? el.GetString() : null,
            OutputType = ReadOutputType(root),
            Dependencies = dependencies,
            Packages = packages
        };
    }

    private static string? ReadOutputType(JsonElement root) {
        if (!root.TryGetProperty("designOptions", out var design) || design.ValueKind != JsonValueKind.Object) {
            return null;
        }

        return design.TryGetProperty("outputType", out var outputType) ? outputType.GetString() : null;
    }
}
