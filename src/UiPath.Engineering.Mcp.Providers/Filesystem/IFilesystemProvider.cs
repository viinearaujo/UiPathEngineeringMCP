namespace UiPath.Engineering.Mcp.Providers.Filesystem;
public interface IFilesystemProvider {
    bool IsPathAllowed(string requestedPath);
    string? FindProjectJson(string projectPath);
    IReadOnlyList<string> FindXamlFiles(string projectPath);
    string ReadAllText(string filePath);
}