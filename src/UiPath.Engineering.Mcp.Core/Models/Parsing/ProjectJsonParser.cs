using System.Text.Json;
using UiPath.Engineering.Mcp.Core.Models;
using UiPath.Engineering.Mcp.Core.Abstractions;

namespace UiPath.Engineering.Mcp.Core.Parsing;

public sealed class ProjectJsonParser
{
    private readonly IFilesystemProvider _filesystem;

    public ProjectJsonParser(IFilesystemProvider filesystem) => _filesystem = filesystem;

    public UiPathProjectModel Parse(string projectJsonPath, string projectRoot)
    {
        var jsonContent = _filesystem.ReadAllText(projectJsonPath);
        using var doc = JsonDocument.Parse(jsonContent);
        var root = doc.RootElement;

        var mainWorkflow = root.TryGetProperty("main", out var main) ? main.GetString() : null;

        var dependencies = new List<string>();
        if (root.TryGetProperty("dependencies", out var deps) && deps.ValueKind == JsonValueKind.Object)
        {
            dependencies = deps.EnumerateObject()
                .Select(p => $"{p.Name} ({p.Value.GetString() ?? "unknown"})")
                .ToList();
        }

        var workflows = _filesystem.FindXamlFiles(projectRoot)
            .Select(Path.GetFileName)
            .Where(f => f is not null)
            .Cast<string>()
            .ToList();

        return new UiPathProjectModel
        {
            ProjectPath = projectRoot,
            ProjectJsonPath = projectJsonPath,
            ProjectName = root.TryGetProperty("name", out var name) ? name.GetString() ?? "Unknown" : "Unknown",
            MainWorkflow = mainWorkflow,
            Dependencies = dependencies,
            Workflows = workflows
        };
    }
}
