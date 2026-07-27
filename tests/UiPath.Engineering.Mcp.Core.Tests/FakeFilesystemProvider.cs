using UiPath.Engineering.Mcp.Core.Abstractions;

namespace UiPath.Engineering.Mcp.Core.Tests;

/// <summary>
/// In-memory <see cref="IFilesystemProvider"/> so Core parsing can be tested without touching disk.
/// </summary>
internal sealed class FakeFilesystemProvider : IFilesystemProvider
{
    public bool Allowed { get; set; } = true;
    public string? ProjectJsonPath { get; set; }
    public List<string> XamlFiles { get; } = [];
    public Dictionary<string, string> FileContents { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool IsPathAllowed(string requestedPath) => Allowed;

    public string? FindProjectJson(string projectPath) => ProjectJsonPath;

    public IReadOnlyList<string> FindXamlFiles(string projectPath) => XamlFiles;

    public string ReadAllText(string filePath) =>
        FileContents.TryGetValue(filePath, out var content)
            ? content
            : throw new FileNotFoundException(filePath);
}
