using System.ComponentModel;
using System.Diagnostics;
using ModelContextProtocol.Server;
using UiPath.Engineering.Mcp.Core.Abstractions;
using UiPath.Engineering.Mcp.Core.Docs;
using UiPath.Engineering.Mcp.Core.Models;
using UiPath.Engineering.Mcp.Core.Parsing;
using UiPath.Engineering.Mcp.Core.Planning;
using UiPath.Engineering.Mcp.Providers.UiPathCli;

namespace UiPath.Engineering.Mcp.Tools;

[McpServerToolType]
public sealed class VerifyWorkTool {
    private readonly IFilesystemProvider _filesystem;
    private readonly IProjectModelBuilder _modelBuilder;
    private readonly IUiPathCliProvider _cliProvider;
    private readonly ImplementationPlanStore _planStore;
    private readonly ProjectDocsValidator _docsValidator;

    public VerifyWorkTool(
        IFilesystemProvider filesystem,
        IProjectModelBuilder modelBuilder,
        IUiPathCliProvider cliProvider,
        ImplementationPlanStore planStore,
        ProjectDocsValidator docsValidator) {
        _filesystem = filesystem;
        _modelBuilder = modelBuilder;
        _cliProvider = cliProvider;
        _planStore = planStore;
        _docsValidator = docsValidator;
    }

    [McpServerTool(UseStructuredContent = true), Description("Leave-off bundled check (not on the Copilot default connector). Prefer validate_project(build:false, pack:false) then update_plan_task. Rebuilds the project model, runs CLI validate (optional build), checks expected files, and can mark plan tasks done or blocked. Not the agent done gate.")]
    public async Task<ToolResult> VerifyWork(
        [Description("Absolute path to the UiPath project directory (must contain project.json).")] string projectPath,
        [Description("Implementation-plan task IDs to verify and update (e.g. ['task-1']).")] List<string>? taskIds = null,
        [Description("Additional project-relative files that must exist for verification to pass.")] List<string>? expectedFiles = null,
        [Description("Run CLI build as part of verification? Default false. Prefer validate_project(build:false) plus update_plan_task for the agent loop.")] bool build = false,
        CancellationToken cancellationToken = default) {

        var sw = Stopwatch.StartNew();

        if (ToolResults.GuardProject(_filesystem, projectPath, sw) is { } guardFailure) {
            return guardFailure;
        }

        var plan = _planStore.Load(projectPath);
        var tasks = new List<PlanTask>();
        if (taskIds is { Count: > 0 }) {
            if (plan is null) {
                return ToolResults.Failure(
                    "No implementation plan found for this project.",
                    "Create one first with create_implementation_plan.",
                    sw);
            }

            foreach (var id in taskIds) {
                var task = plan.Tasks.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));
                if (task is null) {
                    return ToolResults.Failure($"Task '{id}' not found in the implementation plan.", sw);
                }
                tasks.Add(task);
            }
        }

        // Fresh model so the checks below see the post-edit state of the project.
        UiPathProjectModel model;
        try {
            model = await _modelBuilder.BuildAsync(projectPath, cancellationToken);
        } catch (Exception ex) {
            // Never surface a raw exception/stack trace to the MCP client.
            return ToolResults.FromException(ex, "Project analysis failed.", sw);
        }

        UiPathCliResult cliResult;
        try {
            cliResult = await _cliProvider.ValidateAsync(projectPath, validate: true, build: build, pack: false, cancellationToken);
        } catch (Exception ex) {
            // CLI unavailable: report the error and leave plan task statuses unchanged.
            return ToolResults.Failure("Validation could not run.", ex.Message, sw);
        }

        var expected = new List<string>();
        if (expectedFiles is not null) {
            expected.AddRange(expectedFiles);
        }
        expected.AddRange(tasks.SelectMany(t => t.TargetFiles));
        expected = expected.Distinct().ToList();

        var missing = expected.Where(f => !ExpectedFileExists(projectPath, f)).ToList();

        var tasksUpdated = new List<object>();
        if (cliResult.Success && missing.Count == 0) {
            var docsFindings = _docsValidator.Validate(projectPath, model);
            var docsErrors = docsFindings.Where(f => f.Severity == DocsFinding.Error).ToList();
            if (docsErrors.Count > 0) {
                return ToolResults.Failure(
                    "Verification passed CLI checks but project docs have error findings; tasks were not marked done.",
                    docsErrors.Select(DocsGate.ToToolError).ToList(),
                    sw);
            }

            var docsWarnings = docsFindings.Where(f => f.Severity == DocsFinding.Warning).Select(f => f.Message).ToList();
            foreach (var task in tasks) {
                task.Status = PlanTask.Done;
                tasksUpdated.Add(new { taskId = task.Id, status = task.Status });
            }
            if (tasksUpdated.Count > 0) {
                _planStore.Save(projectPath, plan!);
            }

            var warnings = cliResult.Warnings.Concat(docsWarnings).ToList();
            return ToolResults.Ok("Verification passed.", new {
                validation = new { success = true, errors = cliResult.Errors, warnings = cliResult.Warnings },
                tasksUpdated,
                expectations = new { @checked = expected.Count, missing },
                docs = new { findings = docsFindings }
            }, sw, warnings);
        }

        var buildOnlyFailure = build
            && cliResult.Validate.Executed && cliResult.Validate.Success
            && cliResult.Build.Executed && !cliResult.Build.Success;

        if (!cliResult.Success && !buildOnlyFailure) {
            foreach (var task in tasks) {
                task.Status = PlanTask.Blocked;
                task.Notes = $"verify_work failed: {string.Join("; ", cliResult.Errors)}";
                tasksUpdated.Add(new { taskId = task.Id, status = task.Status });
            }
            if (tasksUpdated.Count > 0) {
                _planStore.Save(projectPath, plan!);
            }
        }

        // When validation passed but expectations are unmet, tasks stay unchanged:
        // the work is not verified, but there is no validation error to block on.
        var errors = new List<string>(cliResult.Errors);
        errors.AddRange(missing.Select(f => $"Expected file missing: {f}"));

        return new ToolResult {
            Status = "error",
            Summary = cliResult.Success
                ? "Verification failed: expected files are missing."
                : "Verification failed: validation errors.",
            Data = new {
                validation = new { success = cliResult.Success, errors = cliResult.Errors, warnings = cliResult.Warnings },
                tasksUpdated,
                expectations = new { @checked = expected.Count, missing }
            },
            Errors = errors,
            Warnings = cliResult.Warnings,
            DurationMs = sw.ElapsedMilliseconds
        };
    }

    private bool ExpectedFileExists(string projectPath, string relativePath) =>
        ToolResults.TryResolveWithinProject(projectPath, relativePath, out var targetPath)
        && _filesystem.FileExists(targetPath);
}
