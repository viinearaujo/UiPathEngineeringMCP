using System.ComponentModel;
using System.Diagnostics;
using ModelContextProtocol.Server;
using UiPath.Engineering.Mcp.Core;
using UiPath.Engineering.Mcp.Core.Abstractions;
using UiPath.Engineering.Mcp.Core.Models;

namespace UiPath.Engineering.Mcp.Tools;

[McpServerToolType]
public sealed class ManageProjectFileTool {
    public const string Write = "write";
    public const string Edit = "edit";
    public const string Delete = "delete";

    private readonly IFilesystemProvider _filesystem;

    public ManageProjectFileTool(IFilesystemProvider filesystem) => _filesystem = filesystem;

    [McpServerTool(UseStructuredContent = true), Description("Creates, edits, or deletes a .md/.json/.txt file inside a UiPath project. Does not write project.json, implementation-plan files, docs/knowledge, docs/adr, or secret-looking names. Prefer patch_project_json for project.json and manage_project_docs for knowledge/ADRs.")]
    public ToolResult ManageProjectFile(
        [Description("Absolute path to the UiPath project directory (must contain project.json).")] string projectPath,
        [Description("Operation: write, edit, or delete.")] string action,
        [Description("Path of the file relative to the project root, e.g. 'docs/notes.md'.")] string relativePath,
        [Description("Full file content for write; ignored for delete.")] string? content = null,
        [Description("For edit: exact text to find.")] string? oldString = null,
        [Description("For edit: replacement text.")] string? newString = null) {

        var sw = Stopwatch.StartNew();

        if (ToolResults.GuardProject(_filesystem, projectPath, sw) is { } guardFailure) {
            return guardFailure;
        }

        var normalized = action?.Trim().ToLowerInvariant();
        if (normalized is not (Write or Edit or Delete)) {
            return ToolResults.Failure("action must be write, edit, or delete.", sw);
        }

        if (string.IsNullOrWhiteSpace(relativePath)) {
            return ToolResults.Failure("relativePath is required.", sw);
        }

        if (!ToolResults.TryResolveWithinProject(projectPath, relativePath, out var targetPath)) {
            return ToolResults.Failure("relativePath must resolve to a location inside the project directory.", sw);
        }

        return normalized switch {
            Write => WriteFile(relativePath, targetPath, content, sw),
            Edit => EditFile(relativePath, targetPath, oldString, newString, sw),
            _ => DeleteFile(relativePath, targetPath, sw)
        };
    }

    private ToolResult WriteFile(string relativePath, string targetPath, string? content, Stopwatch sw) {
        var policyError = ProjectFilePolicy.ValidateMutatingFile(relativePath, content, requireContent: true);
        if (policyError is not null) {
            return ToolResults.Failure(policyError, sw);
        }

        var directory = Path.GetDirectoryName(targetPath)!;
        _filesystem.CreateDirectory(directory);
        _filesystem.WriteAllText(targetPath, content!);
        return ToolResults.Ok($"Wrote '{relativePath}'.", new { filePath = targetPath, action = Write }, sw);
    }

    private ToolResult EditFile(string relativePath, string targetPath, string? oldString, string? newString, Stopwatch sw) {
        var policyError = ProjectFilePolicy.ValidateMutatingFile(relativePath, newString ?? string.Empty, requireContent: false);
        if (policyError is not null) {
            return ToolResults.Failure(policyError, sw);
        }

        if (string.IsNullOrEmpty(oldString)) {
            return ToolResults.Failure("oldString is required.", sw);
        }

        if (newString is null) {
            return ToolResults.Failure("newString is required.", sw);
        }

        if (ProjectFilePolicy.ContainsRedactedBody(newString)) {
            return ToolResults.Failure("newString contains ***REDACTED*** and must not be written back to disk.", sw);
        }

        if (!_filesystem.FileExists(targetPath)) {
            return ToolResults.Failure($"File '{relativePath}' does not exist in the project.", sw);
        }

        var original = _filesystem.ReadAllText(targetPath);
        var matches = 0;
        var index = 0;
        while ((index = original.IndexOf(oldString, index, StringComparison.Ordinal)) >= 0) {
            matches++;
            index += oldString.Length;
        }

        if (matches == 0) {
            return ToolResults.Failure("oldString was not found in the file.", sw);
        }

        if (matches > 1) {
            return ToolResults.Failure($"oldString matches {matches} locations; make it more specific.", sw);
        }

        var updated = original.Replace(oldString, newString, StringComparison.Ordinal);
        var afterPolicy = ProjectFilePolicy.ValidateMutatingFile(relativePath, updated, requireContent: true);
        if (afterPolicy is not null) {
            return ToolResults.Failure(afterPolicy, sw);
        }

        _filesystem.WriteAllText(targetPath, updated);
        return ToolResults.Ok($"Updated '{relativePath}'.", new { filePath = targetPath, action = Edit, replacements = 1 }, sw);
    }

    private ToolResult DeleteFile(string relativePath, string targetPath, Stopwatch sw) {
        var policyError = ProjectFilePolicy.ValidateMutatingFile(relativePath, content: null, requireContent: false);
        if (policyError is not null) {
            return ToolResults.Failure(policyError, sw);
        }

        if (!_filesystem.FileExists(targetPath)) {
            return ToolResults.Failure($"File '{relativePath}' does not exist in the project.", sw);
        }

        _filesystem.DeleteFile(targetPath);
        return ToolResults.Ok($"Deleted '{relativePath}'.", new { filePath = targetPath, action = Delete }, sw);
    }
}
