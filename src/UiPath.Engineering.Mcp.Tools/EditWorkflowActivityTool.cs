using System.ComponentModel;
using System.Diagnostics;
using ModelContextProtocol.Server;
using UiPath.Engineering.Mcp.Core;
using UiPath.Engineering.Mcp.Core.Abstractions;
using UiPath.Engineering.Mcp.Core.Models;
using UiPath.Engineering.Mcp.Core.Parsing;

namespace UiPath.Engineering.Mcp.Tools;

[McpServerToolType]
public sealed class EditWorkflowActivityTool {
    private readonly IFilesystemProvider _filesystem;

    public EditWorkflowActivityTool(IFilesystemProvider filesystem) {
        _filesystem = filesystem;
    }

    [McpServerTool, Description("Edits a single activity inside an existing .xaml workflow: insert an activity fragment into a container, replace an activity, or remove one. Target the activity by activityId (preferred, from find_activity) or by DisplayName. Use this for surgical changes instead of rewriting the whole file.")]
    public ToolResult EditWorkflowActivity(
        [Description("Absolute path to the UiPath project directory (must contain project.json).")] string projectPath,
        [Description("Path of the .xaml file relative to the project root, e.g. 'Main.xaml'.")] string relativePath,
        [Description("Operation to perform: insert, replace, or remove.")] string operation,
        [Description("DisplayName of the activity to target (for insert: the container). Optional when activityId is supplied; when both are supplied the DisplayName is verified against the ID-resolved activity.")] string? displayName = null,
        [Description("XAML fragment for insert/replace, e.g. '<ui:LogMessage DisplayName=\"Log\" Message=\"Hi\" />'. Unprefixed WF activities and the ui:/x: prefixes are understood without declarations.")] string? fragment = null,
        [Description("Optional activity type (e.g. 'Sequence') to disambiguate when several activities share the DisplayName.")] string? activityType = null,
        [Description("For insert only: where to add the fragment inside the container — first or last (default).")] string position = XamlActivityEditor.Last,
        [Description("Activity ID from find_activity, e.g. 'sequence.1/if.1' — the preferred way to target an activity.")] string? activityId = null) {

        var sw = Stopwatch.StartNew();

        if (ToolResults.GuardProject(_filesystem, projectPath, sw) is { } guardFailure) {
            return guardFailure;
        }

        if (string.IsNullOrWhiteSpace(relativePath)
            || !relativePath.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)) {
            return ToolResults.Failure("relativePath must point to a .xaml file.", sw);
        }

        if (string.IsNullOrWhiteSpace(activityId) && string.IsNullOrWhiteSpace(displayName)) {
            return ToolResults.Failure(new ToolError(
                ToolErrorCodes.InvalidArgument,
                "Pass activityId (from find_activity) or displayName to locate the target activity.",
                "Run find_activity to list activity IDs."), sw);
        }

        var normalizedOperation = operation?.Trim().ToLowerInvariant();
        if (normalizedOperation is not (XamlActivityEditor.Insert or XamlActivityEditor.Replace or XamlActivityEditor.Remove)) {
            return ToolResults.Failure("operation must be insert, replace, or remove.", sw);
        }

        var normalizedPosition = position?.Trim().ToLowerInvariant();
        if (normalizedPosition is not (XamlActivityEditor.First or XamlActivityEditor.Last)) {
            return ToolResults.Failure("position must be first or last.", sw);
        }

        if (!ToolResults.TryResolveWithinProject(projectPath, relativePath, out var targetPath)) {
            return ToolResults.Failure("relativePath must resolve to a location inside the project directory.", sw);
        }

        if (!_filesystem.FileExists(targetPath)) {
            return ToolResults.Failure($"File not found: {targetPath}", sw);
        }

        var original = _filesystem.ReadAllText(targetPath);
        var edit = string.IsNullOrWhiteSpace(activityId)
            ? XamlActivityEditor.Edit(original, normalizedOperation!, displayName!,
                activityType, fragment, normalizedPosition!)
            : XamlActivityEditor.EditById(original, normalizedOperation!, activityId,
                activityType, displayName, fragment, normalizedPosition!);

        if (!edit.Success) {
            return ToFailure(edit, sw);
        }

        _filesystem.WriteAllText(targetPath, edit.UpdatedContent!);

        return ToolResults.Ok(
            $"Activity '{edit.ResolvedId}' {Describe(normalizedOperation!)} in '{relativePath}'.",
            new {
                filePath = targetPath,
                operation = normalizedOperation,
                activityId = edit.ResolvedId,
                targetDisplayName = displayName
            }, sw,
            warnings: ["Activity IDs are per-parse-snapshot: IDs after the edit point may have shifted. Re-run find_activity before follow-up edits."]);
    }

    // Maps the editor's structured failure codes to the ToolError contract.
    internal static ToolResult ToFailure(XamlEditResult edit, Stopwatch sw) {
        if (edit.ErrorCode is null) {
            return ToolResults.Failure(edit.Error!, sw);
        }
        var fixHint = edit.ErrorCode switch {
            ToolErrorCodes.ActivityNotFound => "Run find_activity to list valid activity IDs and display names.",
            ToolErrorCodes.ActivityIdStale => "Re-run find_activity to get fresh IDs, then retry.",
            ToolErrorCodes.AmbiguousActivity => "Pass activityId to target exactly one activity; run find_activity to get IDs.",
            _ => "Correct the arguments and retry."
        };
        return ToolResults.Failure(
            new ToolError(edit.ErrorCode, edit.Error!, fixHint, "find_activity"), sw);
    }

    private static string Describe(string operation) => operation switch {
        XamlActivityEditor.Insert => "received the inserted activity",
        XamlActivityEditor.Replace => "was replaced",
        _ => "was removed"
    };
}
