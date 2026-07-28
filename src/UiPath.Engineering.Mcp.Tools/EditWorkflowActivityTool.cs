using System.ComponentModel;
using System.Diagnostics;
using ModelContextProtocol.Server;
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

    [McpServerTool, Description("Edits a single activity inside an existing .xaml workflow: insert an activity fragment into a container, replace an activity, or remove one. The target activity is located by its DisplayName. Use this for surgical changes instead of rewriting the whole file.")]
    public Task<ToolResult> EditWorkflowActivity(
        [Description("Absolute path to the UiPath project directory (must contain project.json).")] string projectPath,
        [Description("Path of the .xaml file relative to the project root, e.g. 'Main.xaml'.")] string relativePath,
        [Description("Operation to perform: insert, replace, or remove.")] string operation,
        [Description("DisplayName of the activity to target (for insert: the container, e.g. a Sequence, that receives the new activity).")] string displayName,
        [Description("XAML fragment for insert/replace, e.g. '<ui:LogMessage DisplayName=\"Log\" Message=\"Hi\" />'. Unprefixed WF activities and the ui:/x: prefixes are understood without declarations.")] string? fragment = null,
        [Description("Optional activity type (e.g. 'Sequence') to disambiguate when several activities share the DisplayName.")] string? activityType = null,
        [Description("For insert only: where to add the fragment inside the container — first or last (default).")] string position = XamlActivityEditor.Last) {

        var sw = Stopwatch.StartNew();

        if (!_filesystem.IsPathAllowed(projectPath)) {
            return Error("Path not allowed: project path is outside the allowed roots.", sw);
        }

        if (_filesystem.FindProjectJson(projectPath) == null) {
            return Error("project.json not found: not a valid UiPath project directory.", sw);
        }

        if (string.IsNullOrWhiteSpace(relativePath)
            || !relativePath.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)) {
            return Error("relativePath must point to a .xaml file.", sw);
        }

        var normalizedOperation = operation?.Trim().ToLowerInvariant();
        if (normalizedOperation is not (XamlActivityEditor.Insert or XamlActivityEditor.Replace or XamlActivityEditor.Remove)) {
            return Error("operation must be insert, replace, or remove.", sw);
        }

        var normalizedPosition = position?.Trim().ToLowerInvariant();
        if (normalizedPosition is not (XamlActivityEditor.First or XamlActivityEditor.Last)) {
            return Error("position must be first or last.", sw);
        }

        var targetPath = Path.Combine(Path.GetFullPath(projectPath), relativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!PathGuard.IsWithinDirectory(projectPath, targetPath)) {
            return Error("relativePath must resolve to a location inside the project directory.", sw);
        }

        if (!_filesystem.FileExists(targetPath)) {
            return Error($"File not found: {targetPath}", sw);
        }

        var original = _filesystem.ReadAllText(targetPath);
        var edit = XamlActivityEditor.Edit(original, normalizedOperation!, displayName,
            activityType, fragment, normalizedPosition!);

        if (!edit.Success) {
            return Error(edit.Error!, sw);
        }

        _filesystem.WriteAllText(targetPath, edit.UpdatedContent!);

        return Task.FromResult(new ToolResult {
            Status = "success",
            Summary = $"Activity '{displayName}' {Describe(normalizedOperation!)} in '{relativePath}'.",
            Data = new {
                filePath = targetPath,
                operation = normalizedOperation,
                targetDisplayName = displayName
            },
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static string Describe(string operation) => operation switch {
        XamlActivityEditor.Insert => "received the inserted activity",
        XamlActivityEditor.Replace => "was replaced",
        _ => "was removed"
    };

    private static Task<ToolResult> Error(string message, Stopwatch sw) => Task.FromResult(new ToolResult {
        Status = "error",
        Summary = message,
        Errors = [message],
        DurationMs = sw.ElapsedMilliseconds
    });
}
