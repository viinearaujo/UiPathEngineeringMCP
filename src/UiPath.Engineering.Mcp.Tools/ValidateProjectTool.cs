using System.Diagnostics;
using ModelContextProtocol.Server;
using UiPath.Engineering.Mcp.Core.Models;
using UiPath.Engineering.Mcp.Core.Abstractions;
using UiPath.Engineering.Mcp.Core.Parsing;
using UiPath.Engineering.Mcp.Providers.UiPathCli;
using System.ComponentModel;

namespace UiPath.Engineering.Mcp.Tools;

[McpServerToolType]
public sealed class ValidateProjectTool {
    private readonly IUiPathCliProvider _cliProvider;
    private readonly IFilesystemProvider _filesystem;

    public ValidateProjectTool(IUiPathCliProvider cliProvider, IFilesystemProvider filesystem) {
        _cliProvider = cliProvider;
        _filesystem = filesystem;
    }

    [McpServerTool(UseStructuredContent = true), Description("Runs UiPath CLI validate / build / pack and returns structured per-step results plus diagnostics mapped to snapshot activity IDs. Each diagnostic is { activityId, property, message, specFix }. Agent green gate is validate:true, build:false, pack:false, then update_plan_task. Do not use verify_work as the done gate. For an authoritative compile only, use compile_project.")]
    public async Task<ToolResult> ValidateProject(
        [Description("Absolute path to the UiPath project directory.")] string projectPath,
        [Description("Run validate (project diagnostics)?")] bool validate = true,
        [Description("Run build (compile gate)?")] bool build = true,
        [Description("Run pack?")] bool pack = false,
        CancellationToken cancellationToken = default) {

        var sw = Stopwatch.StartNew();

        if (ToolResults.GuardProject(_filesystem, projectPath, sw) is { } guardFailure) {
            return guardFailure;
        }

        try {
            var cliResult = await _cliProvider.ValidateAsync(projectPath, validate, build, pack, cancellationToken);
            var diagnostics = ProjectDiagnostics(projectPath, cliResult);

            return new ToolResult {
                Status = cliResult.Success ? "success" : "error",
                Summary = cliResult.Summary,
                Data = new {
                    success = cliResult.Success,
                    validate = StepData(cliResult.Validate),
                    build = StepData(cliResult.Build),
                    pack = StepData(cliResult.Pack),
                    errors = cliResult.Errors,
                    warnings = cliResult.Warnings,
                    diagnostics,
                    recommendations = BuildRecommendations(cliResult, diagnostics)
                },
                Errors = cliResult.Errors,
                Warnings = cliResult.Warnings,
                DurationMs = sw.ElapsedMilliseconds
            };
        } catch (Exception ex) {
            return ToolResults.FromException(ex, "Project validation failed.", sw);
        }
    }

    private List<object> ProjectDiagnostics(string projectPath, UiPathCliResult cliResult) {
        var mapped = ValidateDiagnosticMapper.Map(projectPath, _filesystem, cliResult.Diagnostics);
        return mapped.Select(ToPayload).ToList();
    }

    private static object ToPayload(ValidateFixDiagnostic diagnostic) => new {
        activityId = diagnostic.ActivityId,
        property = diagnostic.Property,
        message = diagnostic.Message,
        specFix = diagnostic.SpecFix is null ? null : new {
            workflowFile = diagnostic.SpecFix.WorkflowFile,
            properties = diagnostic.SpecFix.Properties,
            hint = diagnostic.SpecFix.Hint
        }
    };

    private static object StepData(CliStepResult step) => new {
        executed = step.Executed,
        success = step.Executed && step.Success,
        errors = step.Errors,
        warnings = step.Warnings
    };

    private static List<string> BuildRecommendations(UiPathCliResult result, List<object> diagnostics) {
        var recommendations = new List<string>();
        AddRecommendation(recommendations, "validate", result.Validate);
        AddRecommendation(recommendations, "build", result.Build);
        AddRecommendation(recommendations, "pack", result.Pack);
        if (diagnostics.Count > 0) {
            recommendations.Add(
                "Apply diagnostics[].specFix to the activity at diagnostics[].activityId (edit_workflow_activity / insert_activities), then re-run validate_project.");
        }

        return recommendations;
    }

    private static void AddRecommendation(List<string> recommendations, string stepName, CliStepResult step) {
        if (step.Executed && !step.Success) {
            recommendations.Add($"Review the {stepName} errors, fix the underlying issues, and re-run validation.");
        }
    }
}
