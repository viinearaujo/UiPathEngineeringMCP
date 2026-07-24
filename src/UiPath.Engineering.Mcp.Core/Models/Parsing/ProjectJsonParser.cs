using System.Text.Json;
using UiPath.Engineering.Mcp.Core.Models;
using UiPath.Engineering.Mcp.Providers.Filesystem;

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

        var model = new UiPathProjectModel
        {
            ProjectPath = projectRoot,
            ProjectJsonPath = projectJsonPath,
            ProjectName = root.TryGetProperty("name", out var name) ? name.GetString() ?? "Unknown" : "Unknown",
            Description = root.TryGetProperty("description", out var desc) ? desc.GetString() : null,
            MainWorkflow = root.TryGetProperty("main", out var main) ? main.GetString() : null
        };

        if (root.TryGetProperty("dependencies", out var deps) && deps.ValueKind == JsonValueKind.Object)
        {
            model.Dependencies = deps.EnumerateObject()
                .Select(p => new DependencyModel { Name = p.Name, Version = p.Value.GetString() ?? "unknown" })
                .ToList();
                
            model.Packages = model.Dependencies.Select(d => new PackageModel { Id = d.Name, Version = d.Version }).ToList();
        }

        // Discover Workflows
        var xamlFiles = _filesystem.FindXamlFiles(projectRoot);
        model.Workflows = xamlFiles.Select(path => new WorkflowModel
        {
            FileName = Path.GetFileName(path),
            FilePath = path,
            IsMain = Path.GetFileName(path).Equals(model.MainWorkflow, StringComparison.OrdinalIgnoreCase)
        }).ToList();

        return model;
    }
}