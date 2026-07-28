using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using UiPath.Engineering.Mcp.Core.Abstractions;
using UiPath.Engineering.Mcp.Core.Models;
using UiPath.Engineering.Mcp.Core.Templates;

namespace UiPath.Engineering.Mcp.Tools;

[McpServerToolType]
public sealed class CreateCodedWorkflowTool {
    private readonly IFilesystemProvider _filesystem;

    public CreateCodedWorkflowTool(IFilesystemProvider filesystem) {
        _filesystem = filesystem;
    }

    [McpServerTool, Description("Adds a Coded Workflow (.cs class inheriting CodedWorkflow with a [Workflow] entry method) or a plain coded source file to an existing UiPath project. Coded workflows are also registered in project.json entryPoints.")]
    public ToolResult AddCodedWorkflow(
        [Description("Absolute path to the UiPath project directory (must contain project.json).")] string projectPath,
        [Description("Class name for the new file; must be a valid C# identifier and becomes the file name (<ClassName>.cs).")] string className,
        [Description("'workflow' for a Coded Workflow entry point, 'source' for a plain helper class.")] string kind = "workflow") {

        var sw = Stopwatch.StartNew();

        if (ToolResults.GuardProject(_filesystem, projectPath, sw) is { } guardFailure) {
            return guardFailure;
        }

        if (kind is not ("workflow" or "source")) {
            return ToolResults.Failure("kind must be 'workflow' or 'source'.", sw);
        }

        if (!CodedWorkflowTemplates.IsValidClassName(className)) {
            return ToolResults.Failure($"'{className}' is not a valid C# class name.", sw);
        }

        var targetPath = Path.Combine(Path.GetFullPath(projectPath), className + ".cs");
        if (_filesystem.FileExists(targetPath)) {
            return ToolResults.Failure($"File already exists: {targetPath}", sw);
        }

        var projectJsonPath = _filesystem.FindProjectJson(projectPath)!;
        string projectName;
        JsonObject projectJson;
        try {
            projectJson = JsonNode.Parse(_filesystem.ReadAllText(projectJsonPath)) as JsonObject
                ?? throw new InvalidDataException("project.json root is not an object.");
            projectName = projectJson["name"]?.GetValue<string>() ?? "UiPathProject";
        } catch (Exception ex) {
            return ToolResults.Failure($"Could not parse project.json: {ex.Message}", sw);
        }

        var namespaceName = CodedWorkflowTemplates.SanitizeNamespace(projectName);
        var content = kind == "workflow"
            ? CodedWorkflowTemplates.CodedWorkflow(namespaceName, className)
            : CodedWorkflowTemplates.CodedSourceFile(namespaceName, className);

        _filesystem.WriteAllText(targetPath, content);

        // Coded workflows are Process-project entry points: register them in
        // project.json so Studio/Assistant recognize the file as an entry point.
        var entryPointRegistered = false;
        if (kind == "workflow") {
            var entryPoints = projectJson["entryPoints"] as JsonArray ?? new JsonArray();
            projectJson["entryPoints"] = entryPoints;
            entryPoints.Add(new JsonObject {
                ["filePath"] = className + ".cs",
                ["uniqueId"] = Guid.NewGuid().ToString(),
                ["input"] = new JsonArray(),
                ["output"] = new JsonArray()
            });
            _filesystem.WriteAllText(projectJsonPath, projectJson.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            entryPointRegistered = true;
        }

        return ToolResults.Ok(
            kind == "workflow"
                ? $"Coded workflow '{className}' added and registered as an entry point."
                : $"Coded source file '{className}' added.",
            new {
                filePath = targetPath,
                @namespace = namespaceName,
                kind,
                entryPointRegistered
            }, sw);
    }
}
