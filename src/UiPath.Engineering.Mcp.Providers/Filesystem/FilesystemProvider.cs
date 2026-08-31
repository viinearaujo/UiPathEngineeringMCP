using UiPath.Engineering.Mcp.Core.Abstractions;
using UiPath.Engineering.Mcp.Core.Models;

namespace UiPath.Engineering.Mcp.Providers.Filesystem;

public sealed class FilesystemProvider : IFilesystemProvider {
    private static readonly string[] IgnoredDirectories =
    [
        ".git",
        ".local",
        ".settings",
        ".objects",
        "bin",
        "obj",
        "node_modules",
        ".vs"
    ];

    private readonly IPathPolicy _pathPolicy;

    public FilesystemProvider(IPathPolicy pathPolicy) => _pathPolicy = pathPolicy;

    public bool IsPathAllowed(string requestedPath) => _pathPolicy.IsAllowed(requestedPath);

    public string? FindProjectJson(string projectPath) {
        var path = _pathPolicy.EnsureAllowed(projectPath);
        if (File.Exists(path) && Path.GetFileName(path).Equals("project.json", StringComparison.OrdinalIgnoreCase)) {
            return path;
        }

        var jsonPath = Path.Combine(path, "project.json");
        return File.Exists(jsonPath) ? jsonPath : null;
    }

    public IReadOnlyList<string> FindXamlFiles(string projectPath) => FindFilesByExtension(projectPath, "*.xaml");

    public IReadOnlyList<string> FindCSharpFiles(string projectPath) => FindFilesByExtension(projectPath, "*.cs");

    private IReadOnlyList<string> FindFilesByExtension(string projectPath, string pattern) {
        var path = _pathPolicy.EnsureAllowed(projectPath);
        if (!Directory.Exists(path)) {
            return [];
        }

        // Enumerate manually so we can skip noise folders (bin/obj/.git/etc.) instead of
        // returning build artifacts and version-control internals as if they were workflows.
        return EnumerateFiles(path, pattern).ToList();
    }

    private static IEnumerable<string> EnumerateFiles(string directory, string pattern) {
        IEnumerable<string> files;
        try {
            files = Directory.EnumerateFiles(directory, pattern);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            files = [];
        }

        foreach (var file in files) {
            yield return file;
        }

        IEnumerable<string> subDirs;
        try {
            subDirs = Directory.EnumerateDirectories(directory);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            yield break;
        }

        foreach (var sub in subDirs) {
            var name = Path.GetFileName(sub);
            if (IgnoredDirectories.Contains(name, StringComparer.OrdinalIgnoreCase)) {
                continue;
            }

            foreach (var file in EnumerateFiles(sub, pattern)) {
                yield return file;
            }
        }
    }

    public DirectoryTreeNode GetDirectoryTree(string root, int maxDepth = 3) {
        var path = _pathPolicy.EnsureAllowed(root);
        return BuildTree(path, maxDepth, depth: 0);
    }

    private static DirectoryTreeNode BuildTree(string path, int maxDepth, int depth) {
        var node = new DirectoryTreeNode {
            Name = Path.GetFileName(path) is { Length: > 0 } name ? name : path,
            Path = path,
            IsDirectory = true
        };

        if (depth >= maxDepth) {
            return node;
        }

        IEnumerable<string> subDirs;
        IEnumerable<string> files;
        try {
            subDirs = Directory.EnumerateDirectories(path);
            files = Directory.EnumerateFiles(path);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            // Inaccessible directory: return the node with no children rather than failing.
            return node;
        }

        foreach (var sub in subDirs) {
            var subName = Path.GetFileName(sub);
            if (IgnoredDirectories.Contains(subName, StringComparer.OrdinalIgnoreCase)) {
                continue;
            }

            node.Children.Add(BuildTree(sub, maxDepth, depth + 1));
        }

        foreach (var file in files) {
            node.Children.Add(new DirectoryTreeNode {
                Name = Path.GetFileName(file),
                Path = file,
                IsDirectory = false
            });
        }

        node.Children.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return node;
    }

    public string ReadAllText(string filePath) {
        var path = _pathPolicy.EnsureAllowed(filePath);
        return File.ReadAllText(path);
    }

    public long GetFileSize(string filePath) {
        var path = _pathPolicy.EnsureAllowed(filePath);
        return new FileInfo(path).Length;
    }

    public DateTime GetLastWriteTimeUtc(string filePath) {
        var path = _pathPolicy.EnsureAllowed(filePath);
        return File.GetLastWriteTimeUtc(path);
    }

    public void CreateDirectory(string path) {
        var canonical = _pathPolicy.EnsureAllowed(path);
        Directory.CreateDirectory(canonical);
    }

    public void WriteAllText(string filePath, string content) {
        var path = _pathPolicy.EnsureAllowed(filePath);
        File.WriteAllText(path, content);
    }

    public void DeleteFile(string filePath) {
        var fullPath = _pathPolicy.EnsureAllowed(filePath);
        if (File.Exists(fullPath)) {
            File.Delete(fullPath);
        }
    }

    public bool FileExists(string path) {
        var canonical = _pathPolicy.EnsureAllowed(path);
        return File.Exists(canonical);
    }
}
