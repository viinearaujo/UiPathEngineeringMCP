using Microsoft.Extensions.Options;
using UiPath.Engineering.Mcp.Core.Configuration;

namespace UiPath.Engineering.Mcp.Providers.Filesystem;
public sealed class FilesystemProvider : IFilesystemProvider {
    private readonly ProjectRootOptions _options;
    public FilesystemProvider(IOptions<ProjectRootOptions> options) => _options = options.Value;

    public bool IsPathAllowed(string requestedPath) {
        var fullPath = Path.GetFullPath(requestedPath);
        return _options.AllowedRoots
            .Select(Path.GetFullPath)
            .Any(root => fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase));
    }

    public string? FindProjectJson(string projectPath) {
        var path = Path.GetFullPath(projectPath);
        if (File.Exists(path) && Path.GetFileName(path).Equals("project.json", StringComparison.OrdinalIgnoreCase))
            return path;
            
        var jsonPath = Path.Combine(path, "project.json");
        return File.Exists(jsonPath) ? jsonPath : null;
    }

    public IReadOnlyList<string> FindXamlFiles(string projectPath) {
        var path = Path.GetFullPath(projectPath);
        if (!Directory.Exists(path)) return [];
        return Directory.EnumerateFiles(path, "*.xaml", SearchOption.AllDirectories).ToList();
    }

    public string ReadAllText(string filePath) => File.ReadAllText(filePath);
}