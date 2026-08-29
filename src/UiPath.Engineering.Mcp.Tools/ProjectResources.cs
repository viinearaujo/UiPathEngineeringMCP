using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using UiPath.Engineering.Mcp.Core;
using UiPath.Engineering.Mcp.Core.Abstractions;
using UiPath.Engineering.Mcp.Core.Parsing;
using UiPath.Engineering.Mcp.Core.Planning;
using UiPath.Engineering.Mcp.Providers.Skills;

namespace UiPath.Engineering.Mcp.Tools;

[McpServerResourceType]
public sealed class ProjectResources {
    private static readonly JsonSerializerOptions JsonOptions = new() {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private readonly IFilesystemProvider _filesystem;
    private readonly IProjectModelBuilder _modelBuilder;
    private readonly ImplementationPlanStore _planStore;
    private readonly ISkillsProvider _skills;

    public ProjectResources(
        IFilesystemProvider filesystem,
        IProjectModelBuilder modelBuilder,
        ImplementationPlanStore planStore,
        ISkillsProvider skills) {
        _filesystem = filesystem;
        _modelBuilder = modelBuilder;
        _planStore = planStore;
        _skills = skills;
    }

    [McpServerResource(UriTemplate = "uipath://skills/{name}", MimeType = "text/markdown", Name = "skill")]
    [Description("UiPath skill playbook (SKILL.md).")]
    public async Task<string> GetSkill(string name, CancellationToken cancellationToken = default) {
        var result = await _skills.ReadAsync(name, file: null, cancellationToken);
        if (!result.Success) {
            return result.ErrorMessage ?? "Skill read failed.";
        }

        var (redacted, _) = SecretRedactor.Redact(result.Content);
        return redacted;
    }

    [McpServerResource(UriTemplate = "uipath://project/{projectPath}/model", MimeType = "application/json", Name = "project-model")]
    [Description("Summary project model (no activity trees).")]
    public async Task<string> GetProjectModel(string projectPath, CancellationToken cancellationToken = default) {
        if (!_filesystem.IsPathAllowed(projectPath) || _filesystem.FindProjectJson(projectPath) is null) {
            return "Invalid UiPath project directory.";
        }

        var model = await _modelBuilder.BuildAsync(projectPath, cancellationToken);
        return JsonSerializer.Serialize(ProjectAnalysisView.ToSummary(model), JsonOptions);
    }

    [McpServerResource(UriTemplate = "uipath://project/{projectPath}/plan", MimeType = "application/json", Name = "project-plan")]
    [Description("The project's docs/implementation-plan.json.")]
    public string GetProjectPlan(string projectPath) {
        if (!_filesystem.IsPathAllowed(projectPath) || _filesystem.FindProjectJson(projectPath) is null) {
            return "Invalid UiPath project directory.";
        }

        var path = ImplementationPlanStore.GetJsonPath(projectPath);
        if (!File.Exists(path)) {
            return "No implementation plan at docs/implementation-plan.json. Create one with create_implementation_plan only if none exists.";
        }

        return File.ReadAllText(path);
    }

    [McpServerResource(UriTemplate = "uipath://project/{projectPath}/workflow/{relativePath}", MimeType = "text/plain", Name = "project-workflow")]
    [Description("Text of a project file (redacted). Prefer this over a stale model for existence.")]
    public string GetWorkflow(string projectPath, string relativePath) {
        if (!_filesystem.IsPathAllowed(projectPath) || _filesystem.FindProjectJson(projectPath) is null) {
            return "Invalid UiPath project directory.";
        }

        var fileName = Path.GetFileName(relativePath);
        var extension = Path.GetExtension(relativePath);
        if (fileName.StartsWith(".env", StringComparison.OrdinalIgnoreCase)
            || fileName.Contains("credentials", StringComparison.OrdinalIgnoreCase)
            || extension is ".pem" or ".key") {
            return $"'{relativePath}' looks like a secret or key file and cannot be read.";
        }

        if (!ToolResults.TryResolveWithinProject(projectPath, relativePath, out var targetPath)) {
            return "relativePath must resolve to a location inside the project directory.";
        }

        if (!_filesystem.FileExists(targetPath)) {
            return $"File '{relativePath}' does not exist in the project.";
        }

        var raw = _filesystem.ReadAllText(targetPath);
        var (redacted, _) = SecretRedactor.Redact(raw);
        return redacted;
    }
}
