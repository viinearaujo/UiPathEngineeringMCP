using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using UiPath.Engineering.Mcp.Core;
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

    [McpServerTool(UseStructuredContent = true), Description("Adds a coded workflow (.cs inheriting CodedWorkflow with [Workflow], registered in project.json entryPoints), a coded test case ([TestCase], registered in designOptions.fileInfoCollection — never entryPoints), or a plain coded source file to an existing UiPath project. Process projects default kind=test files to Tests\\; pass relativeFolder for other layouts.")]
    public ToolResult AddCodedWorkflow(
        [Description("Absolute path to the UiPath project directory (must contain project.json).")] string projectPath,
        [Description("Class name for the new file; must be a valid C# identifier and becomes the file stem (<ClassName>.cs). Paths belong in relativeFolder, not in className.")] string className,
        [Description("'workflow' for a Coded Workflow entry point, 'test' for a coded test case (fileInfoCollection only; Process projects default to Tests\\), 'source' for a plain helper class.")] string kind = "workflow",
        [Description("Optional project-relative folder (e.g. 'Tests' or 'Models'). Omitted: Process + kind=test defaults to Tests; otherwise project root. Pass an empty string to force the project root.")] string? relativeFolder = null) {

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

        var folder = ResolveFolder(kind, relativeFolder, projectJson);
        var relativeNormalized = string.IsNullOrEmpty(folder)
            ? className + ".cs"
            : ProjectFilePolicy.NormalizeRelativePath(folder + "/" + className + ".cs");
        var relativeStudio = relativeNormalized.Replace('/', '\\');

        if (!ToolResults.TryResolveWithinProject(projectPath, relativeNormalized, out var targetPath)) {
            return ToolResults.Failure("relativeFolder must resolve to a location inside the project directory.", sw);
        }

        if (_filesystem.FileExists(targetPath)) {
            return ToolResults.Failure($"File already exists: {targetPath}", sw);
        }

        var namespaceName = CodedWorkflowTemplates.SanitizeNamespace(projectName);
        var content = kind switch {
            CodedFileKind.Test => CodedWorkflowTemplates.CodedTestCase(namespaceName, className),
            CodedFileKind.Source => CodedWorkflowTemplates.CodedSourceFile(namespaceName, className),
            _ => CodedWorkflowTemplates.CodedWorkflow(namespaceName, className)
        };

        var directory = Path.GetDirectoryName(targetPath)!;
        _filesystem.CreateDirectory(directory);
        _filesystem.WriteAllText(targetPath, content);

        var entryPointRegistered = false;
        var testCaseRegistered = false;
        if (kind == CodedFileKind.Workflow) {
            var entryPoints = projectJson["entryPoints"] as JsonArray ?? new JsonArray();
            projectJson["entryPoints"] = entryPoints;
            entryPoints.Add(new JsonObject {
                ["filePath"] = relativeStudio,
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
                filePath: relativeStudio);
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
                relativePath = relativeStudio,
                @namespace = namespaceName,
                kind,
                entryPointRegistered,
                testCaseRegistered
            }, sw);
    }

    private static string ResolveFolder(string kind, string? relativeFolder, JsonObject projectJson) {
        if (relativeFolder is not null) {
            return ProjectFilePolicy.NormalizeRelativePath(relativeFolder);
        }

        if (kind == CodedFileKind.Test && IsProcessOutputType(projectJson)) {
            return "Tests";
        }

        return string.Empty;
    }

    private static bool IsProcessOutputType(JsonObject projectJson) {
        var outputType = (projectJson["designOptions"] as JsonObject)?["outputType"]?.GetValue<string>();
        return string.Equals(outputType, "Process", StringComparison.OrdinalIgnoreCase);
    }
}
