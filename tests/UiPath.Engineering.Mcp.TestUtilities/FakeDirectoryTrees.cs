using UiPath.Engineering.Mcp.Core.Models;

namespace UiPath.Engineering.Mcp.TestUtilities;

public static class FakeDirectoryTrees {
    public static DirectoryTreeNode FromKnownFiles(string root, IEnumerable<string> filePaths, int maxDepth) =>
        FromPaths(root, filePaths, maxDepth);

    public static DirectoryTreeNode FromPaths(string root, IEnumerable<string> filePaths, int maxDepth) {
        var node = new DirectoryTreeNode {
            Name = Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) is { Length: > 0 } name
                ? name
                : root,
            Path = root,
            IsDirectory = true
        };

        var rootNormalized = Normalize(root);
        foreach (var file in filePaths.Distinct(StringComparer.OrdinalIgnoreCase)) {
            var fileNormalized = Normalize(file);
            if (!IsUnder(rootNormalized, fileNormalized)) {
                continue;
            }

            var relative = fileNormalized.Length == rootNormalized.Length
                ? string.Empty
                : fileNormalized[(rootNormalized.Length + 1)..];
            var parts = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Where(p => p.Length > 0)
                .ToArray();
            if (parts.Length == 0 || parts.Length > maxDepth) {
                continue;
            }

            AddPath(node, file, parts, 0);
        }

        return node;
    }

    private static void AddPath(DirectoryTreeNode parent, string filePath, string[] parts, int index) {
        var name = parts[index];
        var isFile = index == parts.Length - 1;
        var childPath = isFile
            ? filePath
            : Path.Combine(parent.Path, name);
        var existing = parent.Children.FirstOrDefault(c =>
            string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)
            && c.IsDirectory == !isFile);
        if (existing is null) {
            existing = new DirectoryTreeNode {
                Name = name,
                Path = childPath,
                IsDirectory = !isFile
            };
            parent.Children.Add(existing);
        }

        if (!isFile) {
            AddPath(existing, filePath, parts, index + 1);
        }
    }

    private static string Normalize(string path) =>
        path.Replace('/', Path.DirectorySeparatorChar)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static bool IsUnder(string rootNormalized, string candidate) {
        if (string.Equals(rootNormalized, candidate, StringComparison.OrdinalIgnoreCase)) {
            return true;
        }

        var prefix = rootNormalized + Path.DirectorySeparatorChar;
        return candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }
}
