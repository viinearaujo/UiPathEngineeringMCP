using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Server;
using UiPath.Engineering.Mcp.Core;
using UiPath.Engineering.Mcp.Core.Abstractions;
using UiPath.Engineering.Mcp.Core.Docs;
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
    private readonly IPathPolicy _pathPolicy;
    private readonly IProjectModelBuilder _modelBuilder;
    private readonly ImplementationPlanStore _planStore;
    private readonly ISkillsProvider _skills;
    private readonly ProjectKnowledgeStore _knowledge;
    private readonly ProjectAdrStore _adrs;
    private readonly ProjectDocsValidator _docsValidator;
    private readonly ILogger<ProjectResources> _logger;

    public ProjectResources(
        IFilesystemProvider filesystem,
        IPathPolicy pathPolicy,
        IProjectModelBuilder modelBuilder,
        ImplementationPlanStore planStore,
        ISkillsProvider skills,
        ProjectKnowledgeStore knowledge,
        ProjectAdrStore adrs,
        ProjectDocsValidator docsValidator,
        ILogger<ProjectResources>? logger = null) {
        _filesystem = filesystem;
        _pathPolicy = pathPolicy;
        _modelBuilder = modelBuilder;
        _planStore = planStore;
        _skills = skills;
        _knowledge = knowledge;
        _adrs = adrs;
        _docsValidator = docsValidator;
        _logger = logger ?? NullLogger<ProjectResources>.Instance;
    }

    [McpServerResource(UriTemplate = "uipath://skills/{name}", MimeType = "text/markdown", Name = "skill")]
    [Description("UiPath skill playbook (SKILL.md).")]
    public async Task<string> GetSkill(string name, CancellationToken cancellationToken = default) {
        try {
            var result = await _skills.ReadAsync(name, file: null, cancellationToken);
            if (!result.Success) {
                return result.ErrorMessage ?? "Skill read failed.";
            }

            var (redacted, _) = SecretRedactor.Redact(result.Content);
            return redacted;
        } catch (OperationCanceledException) {
            throw;
        } catch (Exception ex) {
            return ResourceFailure("skill", "Skill read failed.", ex);
        }
    }

    [McpServerResource(UriTemplate = "uipath://project/{projectPath}/model", MimeType = "application/json", Name = "project-model")]
    [Description("Summary project model (no activity trees).")]
    public async Task<string> GetProjectModel(string projectPath, CancellationToken cancellationToken = default) {
        try {
            if (!_filesystem.IsPathAllowed(projectPath) || _filesystem.FindProjectJson(projectPath) is null) {
                return "Invalid UiPath project directory.";
            }

            var model = await _modelBuilder.BuildAsync(projectPath, cancellationToken);
            return JsonSerializer.Serialize(ProjectAnalysisView.ToSummary(model), JsonOptions);
        } catch (OperationCanceledException) {
            throw;
        } catch (Exception ex) {
            return ResourceFailure("project-model", "Project model failed.", ex);
        }
    }

    [McpServerResource(UriTemplate = "uipath://project/{projectPath}/plan", MimeType = "application/json", Name = "project-plan")]
    [Description("The project's docs/implementation-plan.json.")]
    public string GetProjectPlan(string projectPath) {
        try {
            if (!_filesystem.IsPathAllowed(projectPath) || _filesystem.FindProjectJson(projectPath) is null) {
                return "Invalid UiPath project directory.";
            }

            var relative = $"{ImplementationPlanStore.PlanDirectoryName}/{ImplementationPlanStore.PlanJsonFileName}";
            if (!_pathPolicy.TryResolveWithinProject(projectPath, relative, out var path)) {
                return "Invalid UiPath project directory.";
            }

            if (!_filesystem.FileExists(path)) {
                return "No implementation plan at docs/implementation-plan.json. Create one with create_implementation_plan only if none exists.";
            }

            var planSize = _filesystem.GetFileSize(path);
            if (_pathPolicy.ExceedsMaxSize(planSize)) {
                return FileReadLimits.OversizedMessage("docs/implementation-plan.json", planSize);
            }

            return _filesystem.ReadAllText(path);
        } catch (OperationCanceledException) {
            throw;
        } catch (Exception ex) {
            return ResourceFailure("project-plan", "Project plan read failed.", ex);
        }
    }

    [McpServerResource(UriTemplate = "uipath://project/{projectPath}/workflow/{relativePath}", MimeType = "text/plain", Name = "project-workflow")]
    [Description("Text of a project file (redacted). Prefer this over a stale model for existence.")]
    public string GetWorkflow(string projectPath, string relativePath) {
        try {
            if (!_filesystem.IsPathAllowed(projectPath) || _filesystem.FindProjectJson(projectPath) is null) {
                return "Invalid UiPath project directory.";
            }

            if (_pathPolicy.IsSecretName(relativePath)) {
                return PathPolicy.SecretReadRefusal(relativePath);
            }

            if (!_pathPolicy.TryResolveWithinProject(projectPath, relativePath, out var targetPath)) {
                return "relativePath must resolve to a location inside the project directory.";
            }

            if (!_filesystem.FileExists(targetPath)) {
                return $"File '{relativePath}' does not exist in the project.";
            }

            var size = _filesystem.GetFileSize(targetPath);
            if (_pathPolicy.ExceedsMaxSize(size)) {
                return FileReadLimits.OversizedMessage(relativePath, size);
            }

            var raw = _filesystem.ReadAllText(targetPath);
            var (redacted, _) = SecretRedactor.Redact(raw);
            return redacted;
        } catch (OperationCanceledException) {
            throw;
        } catch (Exception ex) {
            return ResourceFailure("project-workflow", "Workflow read failed.", ex);
        }
    }

    [McpServerResource(UriTemplate = "uipath://project/{projectPath}/knowledge", MimeType = "application/json", Name = "project-knowledge")]
    [Description("Combined knowledge + ADR index and whether generated context is missing or stale.")]
    public async Task<string> GetProjectKnowledge(string projectPath, CancellationToken cancellationToken = default) {
        try {
            if (!_filesystem.IsPathAllowed(projectPath) || _filesystem.FindProjectJson(projectPath) is null) {
                return "Invalid UiPath project directory.";
            }

            var model = await _modelBuilder.BuildAsync(projectPath, cancellationToken);
            var findings = _docsValidator.Validate(projectPath, model);
            var contextStale = findings.Any(f =>
                f.Severity == DocsFinding.Error
                && f.Code == ToolErrorCodes.DocsStale
                && (f.SuggestedTool == "sync_project_context"
                    || (f.TargetFile is not null && (
                        f.TargetFile.EndsWith("AGENTS.md", StringComparison.OrdinalIgnoreCase)
                        || f.TargetFile.EndsWith("project-context.md", StringComparison.OrdinalIgnoreCase)))));
            var contextMissing = findings.Any(f =>
                f.Severity == DocsFinding.Error
                && f.Message.Contains("Generated project context is missing", StringComparison.OrdinalIgnoreCase));

            var payload = new {
                memory = _knowledge.Load(projectPath).Articles,
                adrs = _adrs.Load(projectPath).Adrs,
                context = new {
                    stale = contextStale,
                    missing = contextMissing
                }
            };
            return JsonSerializer.Serialize(payload, JsonOptions);
        } catch (OperationCanceledException) {
            throw;
        } catch (Exception ex) {
            return ResourceFailure("project-knowledge", "Project knowledge read failed.", ex);
        }
    }

    private string ResourceFailure(string resource, string clientMessage, Exception ex) {
        _logger.LogWarning(ex, "Resource {Resource} failed with {ErrorCode}", resource, ToolErrorCodes.OperationFailed);
        return clientMessage;
    }
}
