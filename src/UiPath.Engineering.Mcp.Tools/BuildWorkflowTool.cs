using System.ComponentModel;
using System.Diagnostics;
using ModelContextProtocol.Server;
using UiPath.Engineering.Mcp.Core.Abstractions;
using UiPath.Engineering.Mcp.Core.Authoring;
using UiPath.Engineering.Mcp.Core.Models;
using UiPath.Engineering.Mcp.Core.Templates;

namespace UiPath.Engineering.Mcp.Tools;

[McpServerToolType]
public sealed class BuildWorkflowTool {
    private readonly IFilesystemProvider _filesystem;
    private readonly IActivityCatalogResolver _catalogResolver;

    public BuildWorkflowTool(IFilesystemProvider filesystem, IActivityCatalogResolver catalogResolver) {
        _filesystem = filesystem;
        _catalogResolver = catalogResolver;
    }

    [McpServerTool(UseStructuredContent = true), Description("Creates a real .xaml workflow file in a UiPath project from a JSON activity spec. Run validate_activity_spec first to dry-run the spec and see every violation before writing. Spec shape: { name, properties, children, variables (root only), catches (TryCatch only), else (If), cases/default (Switch), arguments (InvokeWorkflowFile) }. Strings enclosed in square brackets ([expr]) are interpreted as expressions in the project's configured expression language. All other values are treated as literals.")]
    public async Task<ToolResult> BuildWorkflow(
        [Description("Absolute path to the UiPath project directory (must contain project.json).")] string projectPath,
        [Description("Path of the .xaml file to create relative to the project root, e.g. 'Workflows/Process.xaml'.")] string relativePath,
        [Description("JSON activity spec describing the workflow, e.g. { \"name\": \"Sequence\", \"children\": [...] }. Run validate_activity_spec on it first.")] string specJson,
        [Description("Allow replacing an existing file at relativePath. When false (default), an existing file is never overwritten.")] bool overwrite = false,
        CancellationToken cancellationToken = default) {

        var sw = Stopwatch.StartNew();

        if (ToolResults.GuardProject(_filesystem, projectPath, sw) is { } guardFailure) {
            return guardFailure;
        }

        if (string.IsNullOrWhiteSpace(relativePath)
            || !relativePath.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)) {
            return ToolResults.Failure("relativePath must point to a .xaml file.", sw);
        }

        if (!ToolResults.TryResolveWithinProject(projectPath, relativePath, out var targetPath)) {
            return ToolResults.Failure("relativePath must resolve to a location inside the project directory.", sw);
        }

        if (_filesystem.FileExists(targetPath) && !overwrite) {
            return ToolResults.Failure(
                $"File already exists: {targetPath}. Pass overwrite: true to replace it.", sw);
        }

        if (!SpecJson.TryDeserialize(specJson, out var spec, out var deserializeError)) {
            return ToolResults.Failure(deserializeError!, sw);
        }

        var catalog = await _catalogResolver.ResolveAsync(projectPath, cancellationToken);
        var xamlClass = XamlWorkflowTemplates.ToXamlClassName(relativePath);
        var build = XamlBuilder.RenderWorkflowFile(spec!, xamlClass, catalog);
        if (!build.Success) {
            return ToolResults.Failure($"The activity spec has {build.Errors.Count} violation(s).", build.Errors, sw);
        }

        var directory = Path.GetDirectoryName(targetPath)!;
        _filesystem.CreateDirectory(directory);
        _filesystem.WriteAllText(targetPath, build.Xaml!);

        var activitiesUsed = new List<string>();
        ValidateActivitySpecTool.CollectActivities(spec!, activitiesUsed, catalog);

        return ToolResults.Ok(
            $"Workflow '{relativePath}' created; it uses {activitiesUsed.Count} distinct activity type(s).",
            new {
                filePath = targetPath,
                xamlClass,
                activitiesUsed
            }, sw);
    }
}
