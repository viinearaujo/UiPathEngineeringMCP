using Microsoft.Extensions.Options;
using UiPath.Engineering.Mcp.Core.Configuration;
using UiPath.Engineering.Mcp.Core.Abstractions;
using UiPath.Engineering.Mcp.Core.Models;

namespace UiPath.Engineering.Mcp.Providers.Filesystem;

public sealed class FilesystemProvider : IFilesystemProvider
{
    private static readonly string[] IgnoredDirectories =
    [
        ".git", ".local", ".settings", ".objects", "bin", "obj", "node_modules", ".vs"
    ];

    private readonly ProjectRootOptions _options;

    public FilesystemProvider(IOptions<ProjectRootOptions> options) => _options = options.Value;

    public bool IsPathAllowed(string requestedPath)
    {
        if (string.IsNullOrWhiteSpace(requestedPath))
        {
            return false;
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(requestedPath);
        }
        catch
        {
            return false;
        }

        return _options.AllowedRoots
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(Path.GetFullPath)
            .Any(root => IsWithin(root, fullPath));
    }

    // Ensures the requested path is the root itself or a child of it, guarding against
    // prefix false-positives like root "C:\foo" incorrectly allowing "C:\foobar".
    private static bool IsWithin(string root, string candidate)
    {
        var normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedCandidate = candidate.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (string.Equals(normalizedRoot, normalizedCandidate, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return normalizedCandidate.StartsWith(
            normalizedRoot + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
    }

    public string? FindProjectJson(string projectPath)
    {
        var path = Path.GetFullPath(projectPath);
        if (File.Exists(path) && Path.GetFileName(path).Equals("project.json", StringComparison.OrdinalIgnoreCase))
        {
            return path;
        }

        var jsonPath = Path.Combine(path, "project.json");
        return File.Exists(jsonPath) ? jsonPath : null;
    }

    public IReadOnlyList<string> FindXamlFiles(string projectPath)
    {
        var path = Path.GetFullPath(projectPath);
        if (!Directory.Exists(path))
        {
            return [];
        }

        // Enumerate manually so we can skip noise folders (bin/obj/.git/etc.) instead of
        // returning build artifacts and version-control internals as if they were workflows.
        return EnumerateXaml(path).ToList();
    }

    private static IEnumerable<string> EnumerateXaml(string directory)
    {
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(directory, "*.xaml");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            files = [];
        }

        foreach (var file in files)
        {
            yield return file;
        }

        IEnumerable<string> subDirs;
        try
        {
            subDirs = Directory.EnumerateDirectories(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var sub in subDirs)
        {
            var name = Path.GetFileName(sub);
            if (IgnoredDirectories.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var file in EnumerateXaml(sub))
            {
                yield return file;
            }
        }
    }

    public DirectoryTreeNode GetDirectoryTree(string root, int maxDepth = 3)
    {
        var path = Path.GetFullPath(root);
        return BuildTree(path, maxDepth, depth: 0);
    }

    private static DirectoryTreeNode BuildTree(string path, int maxDepth, int depth)
    {
        var node = new DirectoryTreeNode
        {
            Name = Path.GetFileName(path) is { Length: > 0 } name ? name : path,
            Path = path,
            IsDirectory = true
        };

        if (depth >= maxDepth)
        {
            return node;
        }

        IEnumerable<string> subDirs;
        IEnumerable<string> files;
        try
        {
            subDirs = Directory.EnumerateDirectories(path);
            files = Directory.EnumerateFiles(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Inaccessible directory: return the node with no children rather than failing.
            return node;
        }

        foreach (var sub in subDirs)
        {
            var subName = Path.GetFileName(sub);
            if (IgnoredDirectories.Contains(subName, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            node.Children.Add(BuildTree(sub, maxDepth, depth + 1));
        }

        foreach (var file in files)
        {
            node.Children.Add(new DirectoryTreeNode
            {
                Name = Path.GetFileName(file),
                Path = file,
                IsDirectory = false
            });
        }

        node.Children.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return node;
    }

    public string ReadAllText(string filePath) => File.ReadAllText(filePath);

    public DateTime GetLastWriteTimeUtc(string filePath) => File.GetLastWriteTimeUtc(filePath);

    public void CreateDirectory(string path)
    {
        EnsureAllowed(path);
        Directory.CreateDirectory(Path.GetFullPath(path));
    }

    public void WriteAllText(string filePath, string content)
    {
        EnsureAllowed(filePath);
        File.WriteAllText(Path.GetFullPath(filePath), content);
    }

    public bool FileExists(string path) => File.Exists(Path.GetFullPath(path));

    private void EnsureAllowed(string path)
    {
        if (!IsPathAllowed(path))
        {
            throw new UnauthorizedAccessException($"Path is outside the configured allowed roots: {path}");
        }
    }
}
