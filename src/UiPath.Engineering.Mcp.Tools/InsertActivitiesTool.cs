using System.ComponentModel;
using System.Diagnostics;
using ModelContextProtocol.Server;
using UiPath.Engineering.Mcp.Core;
using UiPath.Engineering.Mcp.Core.Abstractions;
using UiPath.Engineering.Mcp.Core.Authoring;
using UiPath.Engineering.Mcp.Core.Models;
using UiPath.Engineering.Mcp.Core.Parsing;

namespace UiPath.Engineering.Mcp.Tools;

[McpServerToolType]
public sealed class InsertActivitiesTool {
    private readonly IFilesystemProvider _filesystem;

    public InsertActivitiesTool(IFilesystemProvider filesystem) {
        _filesystem = filesystem;
    }

    [McpServerTool(UseStructuredContent = true), Description("Inserts activities described by a JSON activity spec into an existing .xaml workflow, as children of the container activity targeted by activityId (preferred, from find_activity) or by DisplayName (the spec-based sibling of edit_workflow_activity). Run validate_activity_spec first to dry-run the spec and see every violation before writing. Spec shape: { name, properties, children, variables (root only), catches (TryCatch only) }. A root Sequence without variables inserts its children directly; any other root is inserted as a single node. Strings enclosed in square brackets ([expr]) are interpreted as expressions; all other values are literals.")]
    public ToolResult InsertActivities(
        [Description("Absolute path to the UiPath project directory (must contain project.json).")] string projectPath,
        [Description("Path of the .xaml file relative to the project root, e.g. 'Main.xaml'.")] string relativePath,
        [Description("JSON activity spec describing what to insert, e.g. { \"name\": \"Sequence\", \"children\": [...] }. Run validate_activity_spec on it first.")] string specJson,
        [Description("DisplayName of the container activity that receives the new activities. Optional when activityId is supplied.")] string? displayName = null,
        [Description("Activity ID of the container, from find_activity — the preferred way to target it.")] string? activityId = null,
        [Description("Where to add the activities inside the container — first or last (default).")] string position = XamlActivityEditor.Last,
        [Description("Optional activity type (e.g. 'Sequence') to disambiguate when several activities share the DisplayName.")] string? activityType = null) {

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
                "Pass activityId (from find_activity) or displayName to locate the target container.",
                "Run find_activity to list activity IDs."), sw);
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

        if (!SpecJson.TryDeserialize(specJson, out var spec, out var deserializeError)) {
            return ToolResults.Failure(deserializeError!, sw);
        }

        if (!TryRenderFragment(spec!, out var fragment, out var renderErrors)) {
            return ToolResults.Failure($"The activity spec has {renderErrors.Count} violation(s).", renderErrors, sw);
        }

        var original = _filesystem.ReadAllText(targetPath);
        var edit = string.IsNullOrWhiteSpace(activityId)
            ? XamlActivityEditor.Edit(original, XamlActivityEditor.Insert, displayName!,
                activityType, fragment, normalizedPosition!)
            : XamlActivityEditor.EditById(original, XamlActivityEditor.Insert, activityId,
                activityType, displayName, fragment, normalizedPosition!);

        if (!edit.Success) {
            return EditWorkflowActivityTool.ToFailure(edit, sw);
        }

        _filesystem.WriteAllText(targetPath, edit.UpdatedContent!);

        return ToolResults.Ok(
            $"Spec-based activities inserted into '{edit.ResolvedId}' in '{relativePath}'.",
            new {
                filePath = targetPath,
                operation = XamlActivityEditor.Insert,
                activityId = edit.ResolvedId,
                targetDisplayName = displayName
            }, sw,
            warnings: ["Activity IDs are per-parse-snapshot: IDs after the edit point may have shifted. Re-run find_activity before follow-up edits."]);
    }

    // A root Sequence without variables is a convenience wrapper for multiple
    // siblings: render each child separately and concatenate. Anything else
    // (including a Sequence with variables) is rendered as one node.
    private static bool TryRenderFragment(ActivitySpec spec, out string fragment, out List<ToolError> errors) {
        if (string.Equals(spec.Name, "Sequence", StringComparison.OrdinalIgnoreCase)
            && spec.Variables is not { Count: > 0 }) {
            var parts = new List<string>();
            errors = [];
            foreach (var child in spec.Children ?? []) {
                var build = XamlBuilder.RenderFragment(child);
                if (!build.Success) {
                    errors = build.Errors;
                    fragment = string.Empty;
                    return false;
                }
                parts.Add(build.Xaml!);
            }
            fragment = string.Concat(parts);
            return true;
        }

        var rootBuild = XamlBuilder.RenderFragment(spec);
        if (!rootBuild.Success) {
            fragment = string.Empty;
            errors = rootBuild.Errors;
            return false;
        }
        fragment = rootBuild.Xaml!;
        errors = [];
        return true;
    }
}
