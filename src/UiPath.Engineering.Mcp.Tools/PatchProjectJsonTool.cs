using System.ComponentModel;
using System.Diagnostics;
using ModelContextProtocol.Server;
using UiPath.Engineering.Mcp.Core.Abstractions;
using UiPath.Engineering.Mcp.Core.Docs;
using UiPath.Engineering.Mcp.Core.Models;

namespace UiPath.Engineering.Mcp.Tools;

[McpServerToolType]
public sealed class PatchProjectJsonTool {
    private readonly IFilesystemProvider _filesystem;

    public PatchProjectJsonTool(IFilesystemProvider filesystem) => _filesystem = filesystem;

    [McpServerTool(UseStructuredContent = true), Description("Applies one structured operation to project.json: add/remove entry points, upsert/remove dependencies, upsert/remove fileInfoCollection entries, set the exception handler, or set a runtimeOptions value. Never changes expressionLanguage, targetFramework, or schemaVersion.")]
    public ToolResult PatchProjectJson(
        [Description("Absolute path to the UiPath project directory (must contain project.json).")] string projectPath,
        [Description("Operation: add_entry_point, remove_entry_point, upsert_dependency, remove_dependency, upsert_file_info, remove_file_info, set_exception_handler, set_runtime_option.")] string operation,
        [Description("Workflow or test-case path for entry point, fileInfoCollection, or exception handler operations.")] string? filePath = null,
        [Description("Package id for dependency operations.")] string? packageId = null,
        [Description("Package version for upsert_dependency.")] string? version = null,
        [Description("runtimeOptions key for set_runtime_option.")] string? key = null,
        [Description("JSON value for set_runtime_option, e.g. 'true' or '\"GlobalHandler.xaml\"'.")] string? value = null) {

        var sw = Stopwatch.StartNew();

        if (ToolResults.GuardProject(_filesystem, projectPath, sw) is { } guardFailure) {
            return guardFailure;
        }

        var projectJsonPath = _filesystem.FindProjectJson(projectPath)!;
        string json;
        try {
            json = _filesystem.ReadAllText(projectJsonPath);
        } catch (Exception ex) {
            return ToolResults.Failure($"Could not read project.json: {ex.Message}", sw);
        }

        var patch = ProjectJsonPatcher.Apply(json, operation, filePath, packageId, version, key, value);
        if (!patch.Success) {
            return ToolResults.Failure(patch.Error!, sw);
        }

        _filesystem.WriteAllText(projectJsonPath, patch.UpdatedJson!);
        return ToolResults.Ok(patch.Summary ?? "project.json updated.", new {
            filePath = projectJsonPath,
            operation = operation.Trim().ToLowerInvariant()
        }, sw);
    }
}
