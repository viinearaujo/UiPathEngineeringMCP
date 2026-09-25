using System.ComponentModel;
using System.Diagnostics;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using UiPath.Engineering.Mcp.Core.Abstractions;
using UiPath.Engineering.Mcp.Core.Authoring;
using UiPath.Engineering.Mcp.Core.Models;
using UiPath.Engineering.Mcp.Core.Parsing;
using UiPath.Engineering.Mcp.Core.Templates;

namespace UiPath.Engineering.Mcp.Tools;

[McpServerToolType]
public sealed class BuildWorkflowTool {
    private readonly IFilesystemProvider _filesystem;
    private readonly IActivityCatalogResolver _catalogResolver;
    private readonly IProjectModelBuilder? _projectModelBuilder;

    public BuildWorkflowTool(
        IFilesystemProvider filesystem,
        IActivityCatalogResolver catalogResolver,
        IProjectModelBuilder? projectModelBuilder = null) {
        _filesystem = filesystem;
        _catalogResolver = catalogResolver;
        _projectModelBuilder = projectModelBuilder;
    }

    [McpServerTool(
        UseStructuredContent = true,
        Title = "Build Workflow",
        ReadOnly = false,
        Destructive = true,
        Idempotent = false),
     Description("Creates a .xaml from a JSON activity spec. Dry-run with validate_activity_spec; grammar at uipath://authoring/activity-spec. Next: validate_project.")]
    public async Task<ToolResult> BuildWorkflow(
        [Description("Absolute path to the UiPath project directory (must contain project.json).")] string projectPath,
        [Description("Path of the .xaml file to create relative to the project root, e.g. 'Workflows/Process.xaml'.")] string relativePath,
        [Description("JSON activity spec describing the workflow, e.g. { \"name\": \"Sequence\", \"children\": [...] }. Run validate_activity_spec on it first.")] string specJson,
        [Description("Allow replacing an existing file at relativePath. When false (default), an existing file is never overwritten.")] bool overwrite = false,
        [Description("Optional progress sink. The MCP SDK binds this automatically when the client sent a progress token; do not pass it from a caller.")] IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken cancellationToken = default) {

        var sw = Stopwatch.StartNew();
        var reporter = CliToolSupport.ProgressFor(progress, "Resolving the project's activity catalog (may query the CLI), then rendering the XAML.", total: 3);

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
        reporter.Step($"Activity catalog resolved ({catalog.All.Count} activities).");
        var settings = await ProjectXamlSettings.ResolveAsync(_projectModelBuilder, projectPath, cancellationToken);
        var xamlClass = XamlWorkflowTemplates.ToXamlClassName(relativePath);
        var build = XamlBuilder.RenderWorkflowFile(spec!, xamlClass, catalog, settings);
        if (!build.Success) {
            return ToolResults.Failure($"The activity spec has {build.Errors.Count} violation(s).", build.Errors, sw);
        }

        var directory = Path.GetDirectoryName(targetPath)!;
        _filesystem.CreateDirectory(directory);
        _filesystem.WriteAllText(targetPath, build.Xaml!);
        reporter.Step("XAML rendered and written.");

        var activitiesUsed = new List<string>();
        ValidateActivitySpecTool.CollectActivities(spec!, activitiesUsed, catalog);

        return ToolResults.Ok(
            $"Workflow '{relativePath}' created; it uses {activitiesUsed.Count} distinct activity type(s).",
            new {
                filePath = targetPath,
                xamlClass,
                expressionLanguage = settings.ExpressionLanguage.ToString(),
                activitiesUsed
            }, sw,
            build.Warnings.Count > 0 ? build.Warnings : null);
    }
}
