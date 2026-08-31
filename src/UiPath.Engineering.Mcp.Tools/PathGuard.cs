using UiPath.Engineering.Mcp.Core;

namespace UiPath.Engineering.Mcp.Tools;

// Shared path checks for the authoring tools: every written file must resolve
// to a location inside the target project directory.
internal static class PathGuard {
    public static bool IsWithinDirectory(string directory, string candidate) =>
        PathPolicy.IsWithin(directory, candidate, allowEqual: false);
}
