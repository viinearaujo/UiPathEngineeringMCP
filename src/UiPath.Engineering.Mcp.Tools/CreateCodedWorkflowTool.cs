using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using UiPath.Engineering.Mcp.Core.Abstractions;
using UiPath.Engineering.Mcp.Core.Docs;
using UiPath.Engineering.Mcp.Core.Models;
using UiPath.Engineering.Mcp.Core.Templates;

namespace UiPath.Engineering.Mcp.Tools;

[McpServerToolType]
public sealed class CreateCodedWorkflowTool {
    private readonly IFilesystemProvider _filesystem;

    public CreateCodedWorkflowTool(IFilesystemProvider filesystem) {
        _filesystem = filesystem;
    }

    [McpServerTool(UseStructuredContent = true), Description("Adds a coded workflow (.cs inheriting CodedWorkflow with [Workflow], registered in project.json entryPoints), a coded test case ([TestCase], registered in designOptions.fileInfoCollection — never entryPoints), or a plain coded source file to an existing UiPath project.")]
    public ToolResult AddCodedWorkflow(
        [Description("Absolute path to the UiPath project directory (must contain project.json).")] string projectPath,
        [Description("Class name for the new file; must be a valid C# identifier and becomes the file name (<ClassName>.cs).")] string className,
        [Description("'workflow' for a Coded Workflow entry point, 'test' for a coded test case (fileInfoCollection only), 'source' for a plain helper class.")] string kind = "workflow") {

        var sw = Stopwatch.StartNew();

        if (ToolResults.GuardProject(_filesystem, projectPath, sw) is { } guardFailure) {
            return guardFailure;
        }

        if (kind is not (CodedFileKind.Workflow or CodedFileKind.Test or CodedFileKind.Source)) {
            return ToolResults.Failure("kind must be 'workflow', 'test', or 'source'.", sw);
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
        var content = kind switch {
            CodedFileKind.Test => CodedWorkflowTemplates.CodedTestCase(namespaceName, className),
            CodedFileKind.Source => CodedWorkflowTemplates.CodedSourceFile(namespaceName, className),
            _ => CodedWorkflowTemplates.CodedWorkflow(namespaceName, className)
        };

        _filesystem.WriteAllText(targetPath, content);

        var relativeFile = className + ".cs";
        var entryPointRegistered = false;
        var testCaseRegistered = false;
        if (kind == CodedFileKind.Workflow) {
            var entryPoints = projectJson["entryPoints"] as JsonArray ?? new JsonArray();
            projectJson["entryPoints"] = entryPoints;
            entryPoints.Add(new JsonObject {
                ["filePath"] = relativeFile,
                ["uniqueId"] = Guid.NewGuid().ToString(),
                ["input"] = new JsonArray(),
                ["output"] = new JsonArray()
            });
            _filesystem.WriteAllText(projectJsonPath, projectJson.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            entryPointRegistered = true;
        } else if (kind == CodedFileKind.Test) {
            var patch = ProjectJsonPatcher.Apply(
                projectJson.ToJsonString(),
                ProjectJsonPatcher.UpsertFileInfo,
                filePath: relativeFile);
            if (!patch.Success || patch.UpdatedJson is null) {
                return ToolResults.Failure(patch.Error ?? "Could not register the test case in fileInfoCollection.", sw);
            }

            _filesystem.WriteAllText(projectJsonPath, patch.UpdatedJson);
            testCaseRegistered = true;
        }

        var summary = kind switch {
            CodedFileKind.Workflow => $"Coded workflow '{className}' added and registered as an entry point.",
            CodedFileKind.Test => $"Coded test case '{className}' added and registered in fileInfoCollection.",
            _ => $"Coded source file '{className}' added."
        };

        return ToolResults.Ok(
            summary,
            new {
                filePath = targetPath,
                @namespace = namespaceName,
                kind,
                entryPointRegistered,
                testCaseRegistered
            }, sw);
    }
}
