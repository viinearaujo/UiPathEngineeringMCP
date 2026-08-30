using UiPath.Engineering.Mcp.Core.Models;

namespace UiPath.Engineering.Mcp.Core.Tests;

internal static class FakeDirectoryTrees {
    public static DirectoryTreeNode FromPaths(string root, IEnumerable<string> filePaths, int maxDepth) {
        var node = new DirectoryTreeNode {
            Name = Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) is { Length: > 0 } name
                ? name
                : root,
            Path = root,
            IsDirectory = true
        };

        var rootNormalized = Normalize(root);
        foreach (var file in filePaths) {
            var fileNormalized = Normalize(file);
            if (!IsUnder(rootNormalized: rootNormalized, candidate: fileNormalized)) {
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

            if (parts.Length == 1) {
                node.Children.Add(new DirectoryTreeNode {
                    Name = parts[0],
                    Path = file,
                    IsDirectory = false
                });
            }
        }

        return node;
    }

    private static string Normalize(string path) =>
        path.Replace('/', Path.DirectorySeparatorChar)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static bool IsUnder(string rootNormalized, string candidate) {
        if (string.Equals(rootNormalized, candidate, StringComparison.OrdinalIgnoreCase)) {
            return true;
        }

        return candidate.StartsWith(rootNormalized + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || candidate.StartsWith(rootNormalized + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
