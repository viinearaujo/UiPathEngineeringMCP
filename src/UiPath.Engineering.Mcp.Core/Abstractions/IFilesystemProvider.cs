using UiPath.Engineering.Mcp.Core.Models;

namespace UiPath.Engineering.Mcp.Core.Abstractions;

public interface IFilesystemProvider {
    bool IsPathAllowed(string requestedPath);
    string? FindProjectJson(string projectPath);
    IReadOnlyList<string> FindXamlFiles(string projectPath);
    IReadOnlyList<string> FindCSharpFiles(string projectPath);
    string ReadAllText(string filePath);
    long GetFileSize(string filePath);
    DateTime GetLastWriteTimeUtc(string filePath);
    DirectoryTreeNode GetDirectoryTree(string root, int maxDepth = 3);

    // Write operations. These throw UnauthorizedAccessException when the resolved
    // path is outside the configured AllowedRoots.
    void CreateDirectory(string path);
    void WriteAllText(string filePath, string content);
    void DeleteFile(string filePath);
    bool FileExists(string path);
}
