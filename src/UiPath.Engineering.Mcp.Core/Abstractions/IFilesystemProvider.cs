using UiPath.Engineering.Mcp.Core.Models;

namespace UiPath.Engineering.Mcp.Core.Abstractions;

public interface IFilesystemProvider
{
    bool IsPathAllowed(string requestedPath);
    string? FindProjectJson(string projectPath);
    IReadOnlyList<string> FindXamlFiles(string projectPath);
    string ReadAllText(string filePath);
    DateTime GetLastWriteTimeUtc(string filePath);
    DirectoryTreeNode GetDirectoryTree(string root, int maxDepth = 3);
}
