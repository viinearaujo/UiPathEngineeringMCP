namespace UiPath.Engineering.Mcp.Tools;

// Shared path checks for the authoring tools: every written file must resolve
// to a location inside the target project directory.
internal static class PathGuard {
    public static bool IsWithinDirectory(string directory, string candidate) {
        var root = Path.GetFullPath(directory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(candidate).StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }
}
