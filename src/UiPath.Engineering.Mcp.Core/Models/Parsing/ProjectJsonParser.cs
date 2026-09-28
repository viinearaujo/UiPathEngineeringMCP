using System.Text.Json;
using UiPath.Engineering.Mcp.Core;
using UiPath.Engineering.Mcp.Core.Models;
using UiPath.Engineering.Mcp.Core.Abstractions;

namespace UiPath.Engineering.Mcp.Core.Parsing;

public sealed class ProjectJsonParser {
    private readonly IFilesystemProvider _filesystem;

    public ProjectJsonParser(IFilesystemProvider filesystem) => _filesystem = filesystem;

    public UiPathProjectModel Parse(string projectJsonPath, string projectRoot) {
        var size = _filesystem.GetFileSize(projectJsonPath);
        if (size > FileReadLimits.MaxFileBytes) {
            throw new JsonException(FileReadLimits.OversizedMessage("project.json", size));
        }

        var jsonContent = _filesystem.ReadAllText(projectJsonPath);
        if (jsonContent.Length > FileReadLimits.MaxFileBytes) {
            throw new JsonException(FileReadLimits.OversizedMessage("project.json", jsonContent.Length));
        }

        using var doc = JsonDocument.Parse(jsonContent);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) {
            throw new JsonException("project.json must be a JSON object.");
        }

        var mainWorkflow = ReadOptionalString(root, "main");

        var entryPoints = new List<string>();
        if (root.TryGetProperty("entryPoints", out var eps) && eps.ValueKind == JsonValueKind.Array) {
            foreach (var ep in eps.EnumerateArray()) {
                var filePath = ep.ValueKind == JsonValueKind.Object
                    ? ReadOptionalString(ep, "filePath")
                    : null;
                if (!string.IsNullOrWhiteSpace(filePath)) {
                    entryPoints.Add(filePath);
                }
            }
        }

        var fileInfoCollection = new List<string>();
        if (root.TryGetProperty("designOptions", out var designOptions)
            && designOptions.ValueKind == JsonValueKind.Object
            && designOptions.TryGetProperty("fileInfoCollection", out var fic)
            && fic.ValueKind == JsonValueKind.Array) {
            foreach (var item in fic.EnumerateArray()) {
                var fileName = item.ValueKind == JsonValueKind.Object
                    ? ReadOptionalString(item, "fileName")
                    : null;
                if (!string.IsNullOrWhiteSpace(fileName)) {
                    fileInfoCollection.Add(fileName);
                }
            }
        }

        var dependencies = new List<string>();
        var packages = new List<PackageModel>();
        if (root.TryGetProperty("dependencies", out var deps) && deps.ValueKind == JsonValueKind.Object) {
            foreach (var p in deps.EnumerateObject()) {
                var version = p.Value.ValueKind switch {
                    JsonValueKind.String => p.Value.GetString() ?? "unknown",
                    JsonValueKind.Null => "unknown",
                    _ => throw new JsonException($"project.json dependency '{p.Name}' version must be a string.")
                };
                dependencies.Add($"{p.Name} ({version})");
                packages.Add(new PackageModel { Id = p.Name, Version = version });
            }
        }

        return new UiPathProjectModel {
            ProjectPath = projectRoot,
            ProjectJsonPath = projectJsonPath,
            ProjectName = ReadOptionalString(root, "name") ?? "Unknown",
            MainWorkflow = mainWorkflow,
            EntryPoints = entryPoints,
            FileInfoCollection = fileInfoCollection,
            Description = ReadOptionalString(root, "description"),
            TargetFramework = ReadOptionalString(root, "targetFramework"),
            ExpressionLanguage = ReadOptionalString(root, "expressionLanguage"),
            OutputType = ReadOutputType(root),
            Dependencies = dependencies,
            Packages = packages
        };
    }

    private static string? ReadOutputType(JsonElement root) {
        if (!root.TryGetProperty("designOptions", out var design) || design.ValueKind != JsonValueKind.Object) {
            return null;
        }

        return ReadOptionalString(design, "outputType");
    }

    private static string? ReadOptionalString(JsonElement owner, string field) {
        if (!owner.TryGetProperty(field, out var value)) {
            return null;
        }

        return value.ValueKind switch {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Null => null,
            _ => throw new JsonException($"project.json field '{field}' must be a string.")
        };
    }
}
