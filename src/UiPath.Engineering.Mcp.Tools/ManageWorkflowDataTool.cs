using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using ModelContextProtocol.Server;
using UiPath.Engineering.Mcp.Core;
using UiPath.Engineering.Mcp.Core.Abstractions;
using UiPath.Engineering.Mcp.Core.Authoring;
using UiPath.Engineering.Mcp.Core.Models;

namespace UiPath.Engineering.Mcp.Tools;

[McpServerToolType]
public sealed class ManageWorkflowDataTool {
    private readonly IFilesystemProvider _filesystem;

    public ManageWorkflowDataTool(IFilesystemProvider filesystem) {
        _filesystem = filesystem;
    }

    [McpServerTool(
        UseStructuredContent = true,
        Title = "Manage Workflow Data",
        ReadOnly = false,
        Destructive = true,
        Idempotent = false),
     Description("Adds, removes, or renames variables/arguments on an existing .xaml. Next: validate_project.")]
    public ToolResult ManageWorkflowData(
        [Description("Absolute path to the UiPath project directory (must contain project.json).")] string projectPath,
        [Description("Path of the .xaml file relative to the project root, e.g. 'Main.xaml'.")] string relativePath,
        [Description("Operation to perform: add, remove, or rename.")]
        [AllowedValues(WorkflowSurfaceEditor.Add, WorkflowSurfaceEditor.Remove, WorkflowSurfaceEditor.Rename)] string operation,
        [Description("What to manage: variable or argument.")]
        [AllowedValues(WorkflowSurfaceEditor.Variable, WorkflowSurfaceEditor.Argument)] string kind,
        [Description("Name of the variable or argument to add, remove, or rename.")] string name,
        [Description("Type for add, e.g. 'String', 'Int32', 'System.Data.DataTable'. BCL primitives render as x:-prefixed tokens.")] string? type = null,
        [Description("For arguments only: direction In (default), Out, or In/Out.")] string direction = "In",
        [Description("For rename only: the new name.")] string? newName = null,
        [Description("For variables only: optional default value expression.")] string? defaultValue = null) {

        var sw = Stopwatch.StartNew();

        if (ToolResults.GuardProject(_filesystem, projectPath, sw) is { } guardFailure) {
            return guardFailure;
        }

        if (string.IsNullOrWhiteSpace(relativePath)
            || !relativePath.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)) {
            return ToolResults.Failure("relativePath must point to a .xaml file.", sw);
        }

        if (ToolArgs.ParseChoice(operation, "operation", [WorkflowSurfaceEditor.Add, WorkflowSurfaceEditor.Remove, WorkflowSurfaceEditor.Rename], sw, out var normalizedOperation) is { } operationError) {
            return operationError;
        }

        if (ToolArgs.ParseChoice(kind, "kind", [WorkflowSurfaceEditor.Variable, WorkflowSurfaceEditor.Argument], sw, out var normalizedKind) is { } kindError) {
            return kindError;
        }

        if (!ToolResults.TryResolveWithinProject(projectPath, relativePath, out var targetPath)) {
            return ToolResults.Failure("relativePath must resolve to a location inside the project directory.", sw);
        }

        if (!_filesystem.FileExists(targetPath)) {
            return ToolResults.Failure($"File not found: {targetPath}", sw);
        }

        var original = _filesystem.ReadAllText(targetPath);
        var edit = WorkflowSurfaceEditor.Edit(original, normalizedOperation!, normalizedKind!, name,
            type, direction, newName, defaultValue);

        if (!edit.Success) {
            if (edit.ErrorCode is ToolErrorCodes.DataDeclarationConflict or ToolErrorCodes.DataDeclarationNotFound) {
                return ToolResults.Failure(new ToolError(
                    edit.ErrorCode,
                    edit.Error!,
                    edit.ErrorCode == ToolErrorCodes.DataDeclarationConflict
                        ? $"Choose a different name, or remove the existing '{name}' declaration first."
                        : $"Check the name spelling, or list declarations in '{relativePath}' to see what exists."), sw);
            }
            return ToolResults.Failure(edit.Error!, sw);
        }

        _filesystem.WriteAllText(targetPath, edit.UpdatedContent!);

        return ToolResults.Ok(
            $"{Capitalize(normalizedKind!)} '{name}' {Describe(normalizedOperation!)} in '{relativePath}'.",
            new {
                filePath = targetPath,
                operation = normalizedOperation,
                kind = normalizedKind,
                name
            }, sw, edit.Warnings);
    }

    private static string Describe(string operation) => operation switch {
        WorkflowSurfaceEditor.Add => "was added",
        WorkflowSurfaceEditor.Rename => "was renamed",
        _ => "was removed"
    };

    private static string Capitalize(string value) => char.ToUpperInvariant(value[0]) + value[1..];
}
