using System.Diagnostics;
using System.Text.Json;
using UiPath.Engineering.Mcp.Core.Abstractions;
using UiPath.Engineering.Mcp.Core.Models;

namespace UiPath.Engineering.Mcp.Tools;

// Shared construction of the standard ToolResult envelope plus the guard checks
// every tool performs, so each tool keeps only its own logic.
internal static class ToolResults {
    public static ToolResult Ok(string summary, object? data, Stopwatch sw, List<string>? warnings = null) => new() {
        Summary = summary,
        Data = data,
        Warnings = warnings ?? [],
        DurationMs = sw.ElapsedMilliseconds
    };

    public static ToolResult Failure(string summary, string error, Stopwatch sw) => new() {
        Status = "error",
        Summary = summary,
        Errors = [error],
        DurationMs = sw.ElapsedMilliseconds
    };

    public static ToolResult Failure(string message, Stopwatch sw) => Failure(message, message, sw);

    public static ToolResult Failure(string summary, IReadOnlyList<string> errors, Stopwatch sw) => new() {
        Status = "error",
        Summary = summary,
        Errors = [.. errors],
        DurationMs = sw.ElapsedMilliseconds
    };

    // Guard for tools that only need the path to be inside the allowed roots.
    // Returns null when the path is usable.
    public static ToolResult? GuardAllowedPath(IFilesystemProvider filesystem, string path, Stopwatch sw) =>
        filesystem.IsPathAllowed(path)
            ? null
            : Failure("Path not allowed.", "The requested path is outside the allowed project roots.", sw);

    // Guard for tools operating on an existing UiPath project: the path must be
    // allowed and contain a project.json. Returns null when the project is usable.
    public static ToolResult? GuardProject(IFilesystemProvider filesystem, string projectPath, Stopwatch sw) =>
        GuardAllowedPath(filesystem, projectPath, sw)
        ?? (filesystem.FindProjectJson(projectPath) == null
            ? Failure("project.json not found.", "Invalid UiPath project directory.", sw)
            : null);

    // Resolves a project-relative path and verifies it stays inside the project directory.
    public static bool TryResolveWithinProject(string projectPath, string relativePath, out string targetPath) {
        targetPath = Path.Combine(Path.GetFullPath(projectPath), relativePath.Replace('/', Path.DirectorySeparatorChar));
        return PathGuard.IsWithinDirectory(projectPath, targetPath);
    }

    // Maps the standard project-model failure modes to structured results so tools
    // never leak raw exceptions to the MCP client.
    public static ToolResult FromException(Exception ex, string failureSummary, Stopwatch sw) => ex switch {
        FileNotFoundException => Failure("project.json not found.", ex.Message, sw),
        JsonException => Failure("project.json could not be parsed.", $"Invalid JSON in project.json: {ex.Message}", sw),
        _ => Failure(failureSummary, ex.Message, sw)
    };
}
