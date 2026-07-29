using System.ComponentModel;
using System.Diagnostics;
using ModelContextProtocol.Server;
using UiPath.Engineering.Mcp.Core.Abstractions;
using UiPath.Engineering.Mcp.Core.Models;

namespace UiPath.Engineering.Mcp.Tools;

[McpServerToolType]
public sealed class EditWorkflowFileTool {
    private static readonly string[] AllowedExtensions = [".xaml", ".cs"];

    private readonly IFilesystemProvider _filesystem;

    public EditWorkflowFileTool(IFilesystemProvider filesystem) {
        _filesystem = filesystem;
    }

    [McpServerTool, Description("Replaces an exact string in a .xaml or .cs workflow file inside an existing UiPath project. Fails when oldString is not found or matches multiple locations unless replaceAll is true. Prefer this over write_workflow_file for small changes.")]
    public ToolResult EditWorkflowFile(
        [Description("Absolute path to the UiPath project directory (must contain project.json).")] string projectPath,
        [Description("Path of the file relative to the project root, e.g. 'Main.xaml' or 'Workflows/SendEmail.cs'.")] string relativePath,
        [Description("Exact text to find; must match the file content byte-for-byte, including whitespace.")] string oldString,
        [Description("Replacement text.")] string newString,
        [Description("Replace every occurrence instead of requiring exactly one match.")] bool replaceAll = false) {

        var sw = Stopwatch.StartNew();

        if (ToolResults.GuardProject(_filesystem, projectPath, sw) is { } guardFailure) {
            return guardFailure;
        }

        if (string.IsNullOrWhiteSpace(relativePath)) {
            return ToolResults.Failure("relativePath is required.", sw);
        }

        var extension = Path.GetExtension(relativePath);
        if (!AllowedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase)) {
            return ToolResults.Failure($"Only {string.Join(" and ", AllowedExtensions)} files can be edited; got '{extension}'.", sw);
        }

        if (string.IsNullOrEmpty(oldString)) {
            return ToolResults.Failure("oldString is required.", sw);
        }

        if (!ToolResults.TryResolveWithinProject(projectPath, relativePath, out var targetPath)) {
            return ToolResults.Failure("relativePath must resolve to a location inside the project directory.", sw);
        }

        if (!_filesystem.FileExists(targetPath)) {
            return ToolResults.Failure($"File '{relativePath}' does not exist in the project.", sw);
        }

        var content = _filesystem.ReadAllText(targetPath);

        var matches = 0;
        var index = 0;
        while ((index = content.IndexOf(oldString, index, StringComparison.Ordinal)) >= 0) {
            matches++;
            index += oldString.Length;
        }

        if (matches == 0) {
            return ToolResults.Failure(
                "oldString was not found in the file. Read the file first to get its exact content, including whitespace.", sw);
        }

        if (matches > 1 && !replaceAll) {
            return ToolResults.Failure(
                $"oldString matches {matches} locations; make it more specific or pass replaceAll: true.", sw);
        }

        var updated = content.Replace(oldString, newString, StringComparison.Ordinal);
        _filesystem.WriteAllText(targetPath, updated);

        return ToolResults.Ok(
            $"Updated '{relativePath}' ({matches} replacement(s)).",
            new {
                filePath = targetPath,
                replacements = matches,
                bytesWritten = updated.Length
            }, sw);
    }
}
