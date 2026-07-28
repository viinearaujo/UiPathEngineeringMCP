namespace UiPath.Engineering.Mcp.Core.Models;

public sealed class DirectoryTreeNode {
    public string Name { get; init; } = string.Empty;
    public string Path { get; init; } = string.Empty;
    public bool IsDirectory { get; init; }
    public List<DirectoryTreeNode> Children { get; init; } = [];
}
