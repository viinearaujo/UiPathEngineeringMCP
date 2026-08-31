using System.Diagnostics;
using System.Text.Json;
using UiPath.Engineering.Mcp.Core;
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

    public static ToolResult Failure(ToolError error, Stopwatch sw) =>
        Failure(error.Message, [error], sw);

    public static ToolResult Failure(string summary, IReadOnlyList<ToolError> errors, Stopwatch sw) => new() {
        Status = "error",
        Summary = summary,
        Errors = errors.Select(e => $"{e.ErrorCode}: {e.Message} Fix: {e.FixHint}").ToList(),
        ErrorDetails = [.. errors],
        DurationMs = sw.ElapsedMilliseconds
    };

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
            : PathNotAllowed(sw);

    // Guard for tools operating on an existing UiPath project: the path must be
    // allowed and contain a project.json. Returns null when the project is usable.
    public static ToolResult? GuardProject(IFilesystemProvider filesystem, string projectPath, Stopwatch sw) =>
        GuardAllowedPath(filesystem, projectPath, sw)
        ?? (filesystem.FindProjectJson(projectPath) == null
            ? Failure("project.json not found.",
                [new ToolError(
                    ToolErrorCodes.ProjectJsonNotFound,
                    "The directory is not a UiPath project (project.json is missing).",
                    "Pass a UiPath project directory that contains project.json.")],
                sw)
            : null);

    public static ToolResult PathNotAllowed(
        Stopwatch sw,
        string summary = "Path not allowed.",
        string message = "The requested path is outside the allowed project roots.") =>
        Failure(summary, [
            new ToolError(
                ToolErrorCodes.PathNotAllowed,
                message,
                "Pass a path inside Projects:AllowedRoots.")
        ], sw);

    // Resolves a project-relative path and verifies it stays inside the project directory.
    public static bool TryResolveWithinProject(string projectPath, string relativePath, out string targetPath) =>
        PathPolicy.TryResolveProjectRelative(projectPath, relativePath, out targetPath);

    // Maps the standard project-model failure modes to structured results so tools
    // never leak raw exceptions to the MCP client.
    public static ToolResult FromException(Exception ex, string failureSummary, Stopwatch sw) {
        var error = McpToolErrorMapper.ToToolError(ex, failureSummary);
        var summary = ex switch {
            FileNotFoundException => "project.json not found.",
            JsonException => "project.json could not be parsed.",
            UnauthorizedAccessException => "Path not allowed.",
            _ => failureSummary
        };
        return Failure(summary, [error], sw);
    }
}
