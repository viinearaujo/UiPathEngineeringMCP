using UiPath.Engineering.Mcp.Core.Abstractions;
using UiPath.Engineering.Mcp.Core.Models;
using UiPath.Engineering.Mcp.TestUtilities;

namespace UiPath.Engineering.Mcp.Core.Tests;

/// <summary>
/// In-memory <see cref="IFilesystemProvider"/> so Core parsing can be tested without touching disk.
/// </summary>
internal sealed class FakeFilesystemProvider : IFilesystemProvider {
    public bool Allowed { get; set; } = true;
    public List<string>? AllowedRoots { get; set; }
    public string? ProjectJsonPath { get; set; }
    public List<string> XamlFiles { get; } = [];
    public List<string> CSharpFiles { get; } = [];
    public Dictionary<string, string> FileContents { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, long> FileSizes { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, DateTime> WriteTimesUtc { get; } = new(StringComparer.OrdinalIgnoreCase);
    public DirectoryTreeNode? DirectoryTree { get; set; }

    public bool IsPathAllowed(string requestedPath) {
        if (AllowedRoots is { Count: > 0 }) {
            return AllowedRoots.Any(root =>
                requestedPath.StartsWith(root.TrimEnd('/', '\\'), StringComparison.OrdinalIgnoreCase)
                || string.Equals(requestedPath, root, StringComparison.OrdinalIgnoreCase));
        }

        return Allowed;
    }

    private void EnsureAllowed(string path) {
        if (!IsPathAllowed(path)) {
            throw new UnauthorizedAccessException($"Path is outside the configured allowed roots: {path}");
        }
    }

    public string? FindProjectJson(string projectPath) => ProjectJsonPath;

    public IReadOnlyList<string> FindXamlFiles(string projectPath) => XamlFiles;

    public IReadOnlyList<string> FindCSharpFiles(string projectPath) => CSharpFiles;

    public string ReadAllText(string filePath) {
        EnsureAllowed(filePath);
        return FileContents.TryGetValue(filePath, out var content)
            ? content
            : throw new FileNotFoundException(filePath);
    }

    public long GetFileSize(string filePath) {
        EnsureAllowed(filePath);
        if (FileSizes.TryGetValue(filePath, out var size)) {
            return size;
        }
        return FileContents.TryGetValue(filePath, out var content)
            ? content.Length
            : throw new FileNotFoundException(filePath);
    }

    public Exception? GetLastWriteTimeException { get; set; }

    public DateTime GetLastWriteTimeUtc(string filePath) {
        EnsureAllowed(filePath);
        if (GetLastWriteTimeException is not null) {
            throw GetLastWriteTimeException;
        }

        if (WriteTimesUtc.TryGetValue(filePath, out var timestamp)) {
            return timestamp;
        }
        if (FileContents.ContainsKey(filePath)) {
            return DateTime.UnixEpoch;
        }
        throw new FileNotFoundException(filePath);
    }

    public void CreateDirectory(string path) { }

    public void WriteAllText(string filePath, string content) {
        EnsureAllowed(filePath);
        FileContents[filePath] = content;
        WriteTimesUtc[filePath] = DateTime.UtcNow;
    }

    public void DeleteFile(string filePath) {
        FileContents.Remove(filePath);
        FileSizes.Remove(filePath);
        WriteTimesUtc.Remove(filePath);
    }

    public bool FileExists(string path) {
        EnsureAllowed(path);
        return FileContents.ContainsKey(path);
    }

    public DirectoryTreeNode GetDirectoryTree(string root, int maxDepth = 3) {
        if (DirectoryTree is not null) {
            return DirectoryTree;
        }

        return FakeDirectoryTrees.FromPaths(root, FileContents.Keys, maxDepth);
    }
}
