using System.ComponentModel;
using System.Diagnostics;
using ModelContextProtocol.Server;
using UiPath.Engineering.Mcp.Core.Abstractions;
using UiPath.Engineering.Mcp.Core.Models;

namespace UiPath.Engineering.Mcp.Tools;

[McpServerToolType]
public sealed class WriteWorkflowFileTool {
    private static readonly string[] AllowedExtensions = [".xaml", ".cs"];

    private readonly IFilesystemProvider _filesystem;

    public WriteWorkflowFileTool(IFilesystemProvider filesystem) {
        _filesystem = filesystem;
    }

    [McpServerTool, Description("Creates or fully overwrites a .xaml or .cs workflow file inside an existing UiPath project with the supplied content. Use this to modify workflows end-to-end.")]
    public Task<ToolResult> WriteWorkflowFile(
        [Description("Absolute path to the UiPath project directory (must contain project.json).")] string projectPath,
        [Description("Path of the file relative to the project root, e.g. 'Main.xaml' or 'Workflows/SendEmail.xaml'.")] string relativePath,
        [Description("Full new content of the file.")] string content) {

        var sw = Stopwatch.StartNew();

        if (!_filesystem.IsPathAllowed(projectPath)) {
            return Error("Path not allowed: project path is outside the allowed roots.", sw);
        }

        if (_filesystem.FindProjectJson(projectPath) == null) {
            return Error("project.json not found: not a valid UiPath project directory.", sw);
        }

        if (string.IsNullOrWhiteSpace(relativePath)) {
            return Error("relativePath is required.", sw);
        }

        var extension = Path.GetExtension(relativePath);
        if (!AllowedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase)) {
            return Error($"Only {string.Join(" and ", AllowedExtensions)} files can be written; got '{extension}'.", sw);
        }

        var targetPath = Path.Combine(Path.GetFullPath(projectPath), relativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!PathGuard.IsWithinDirectory(projectPath, targetPath)) {
            return Error("relativePath must resolve to a location inside the project directory.", sw);
        }

        var existed = _filesystem.FileExists(targetPath);

        var directory = Path.GetDirectoryName(targetPath)!;
        _filesystem.CreateDirectory(directory);
        _filesystem.WriteAllText(targetPath, content);

        return Task.FromResult(new ToolResult {
            Status = "success",
            Summary = existed ? $"Updated '{relativePath}'." : $"Created '{relativePath}'.",
            Data = new {
                filePath = targetPath,
                bytesWritten = content.Length,
                overwritten = existed
            },
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static Task<ToolResult> Error(string message, Stopwatch sw) => Task.FromResult(new ToolResult {
        Status = "error",
        Summary = message,
        Errors = [message],
        DurationMs = sw.ElapsedMilliseconds
    });
}
