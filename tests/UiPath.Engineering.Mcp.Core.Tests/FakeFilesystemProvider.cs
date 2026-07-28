using UiPath.Engineering.Mcp.Core.Abstractions;
using UiPath.Engineering.Mcp.Core.Models;

namespace UiPath.Engineering.Mcp.Core.Tests;

/// <summary>
/// In-memory <see cref="IFilesystemProvider"/> so Core parsing can be tested without touching disk.
/// </summary>
internal sealed class FakeFilesystemProvider : IFilesystemProvider {
    public bool Allowed { get; set; } = true;
    public string? ProjectJsonPath { get; set; }
    public List<string> XamlFiles { get; } = [];
    public List<string> CSharpFiles { get; } = [];
    public Dictionary<string, string> FileContents { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, DateTime> WriteTimesUtc { get; } = new(StringComparer.OrdinalIgnoreCase);
    public DirectoryTreeNode? DirectoryTree { get; set; }

    public bool IsPathAllowed(string requestedPath) => Allowed;

    public string? FindProjectJson(string projectPath) => ProjectJsonPath;

    public IReadOnlyList<string> FindXamlFiles(string projectPath) => XamlFiles;

    public IReadOnlyList<string> FindCSharpFiles(string projectPath) => CSharpFiles;

    public DirectoryTreeNode GetDirectoryTree(string root, int maxDepth = 3) =>
        DirectoryTree ?? new DirectoryTreeNode { Name = Path.GetFileName(root) ?? root, Path = root, IsDirectory = true };

    public string ReadAllText(string filePath) =>
        FileContents.TryGetValue(filePath, out var content)
            ? content
            : throw new FileNotFoundException(filePath);

    public DateTime GetLastWriteTimeUtc(string filePath) =>
        WriteTimesUtc.TryGetValue(filePath, out var timestamp)
            ? timestamp
            : throw new FileNotFoundException(filePath);

    public void CreateDirectory(string path) { }

    public void WriteAllText(string filePath, string content) => FileContents[filePath] = content;

    public bool FileExists(string path) => FileContents.ContainsKey(path);
}
