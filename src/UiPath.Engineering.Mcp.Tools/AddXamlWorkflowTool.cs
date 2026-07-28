using System.ComponentModel;
using System.Diagnostics;
using ModelContextProtocol.Server;
using UiPath.Engineering.Mcp.Core.Abstractions;
using UiPath.Engineering.Mcp.Core.Models;
using UiPath.Engineering.Mcp.Core.Templates;

namespace UiPath.Engineering.Mcp.Tools;

[McpServerToolType]
public sealed class AddXamlWorkflowTool {
    private readonly IFilesystemProvider _filesystem;

    public AddXamlWorkflowTool(IFilesystemProvider filesystem) {
        _filesystem = filesystem;
    }

    [McpServerTool, Description("Adds a new blank XAML workflow file to an existing UiPath project, with the correct x:Class naming for its location.")]
    public ToolResult AddXamlWorkflow(
        [Description("Absolute path to the UiPath project directory (must contain project.json).")] string projectPath,
        [Description("Workflow file name or relative path within the project, e.g. 'SendEmail.xaml' or 'Workflows/SendEmail.xaml'.")] string fileName) {

        var sw = Stopwatch.StartNew();

        if (ToolResults.GuardProject(_filesystem, projectPath, sw) is { } guardFailure) {
            return guardFailure;
        }

        if (string.IsNullOrWhiteSpace(fileName)) {
            return ToolResults.Failure("fileName is required.", sw);
        }

        var relative = fileName.Replace('\\', '/').Trim('/');
        if (!relative.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)) {
            relative += ".xaml";
        }

        if (!ToolResults.TryResolveWithinProject(projectPath, relative, out var targetPath)) {
            return ToolResults.Failure("fileName must resolve to a location inside the project directory.", sw);
        }

        if (_filesystem.FileExists(targetPath)) {
            return ToolResults.Failure($"File already exists: {targetPath}", sw);
        }

        var content = XamlWorkflowTemplates.BlankWorkflow(XamlWorkflowTemplates.ToXamlClassName(relative));

        var directory = Path.GetDirectoryName(targetPath)!;
        _filesystem.CreateDirectory(directory);
        _filesystem.WriteAllText(targetPath, content);

        return ToolResults.Ok(
            $"Workflow '{relative}' added to the project.",
            new {
                filePath = targetPath,
                xamlClass = XamlWorkflowTemplates.ToXamlClassName(relative)
            }, sw);
    }
}
