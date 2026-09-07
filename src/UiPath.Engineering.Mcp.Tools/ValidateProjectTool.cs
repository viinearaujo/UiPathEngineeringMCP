using System.Diagnostics;
using ModelContextProtocol.Server;
using UiPath.Engineering.Mcp.Core;
using UiPath.Engineering.Mcp.Core.Models;
using UiPath.Engineering.Mcp.Core.Abstractions;
using UiPath.Engineering.Mcp.Core.GapAnalysis;
using UiPath.Engineering.Mcp.Core.Parsing;
using UiPath.Engineering.Mcp.Providers.UiPathCli;
using System.ComponentModel;

namespace UiPath.Engineering.Mcp.Tools;

[McpServerToolType]
public sealed class ValidateProjectTool {
    private readonly IUiPathCliProvider _cliProvider;
    private readonly IFilesystemProvider _filesystem;
    private readonly IProjectModelBuilder? _modelBuilder;

    public ValidateProjectTool(
        IUiPathCliProvider cliProvider,
        IFilesystemProvider filesystem,
        IProjectModelBuilder? modelBuilder = null) {
        _cliProvider = cliProvider;
        _filesystem = filesystem;
        _modelBuilder = modelBuilder;
    }

    [McpServerTool(UseStructuredContent = true), Description("Runs UiPath CLI validate / build / pack and returns structured per-step results plus diagnostics mapped to snapshot activity IDs. Each diagnostic is { activityId, property, message, specFix }. Agent green gate is validate:true (the default), pack:false, then analyze_project_gaps then update_plan_task. Do not use verify_work as the done gate. For an authoritative CLI compile, pass build:true (compile_project is a leave-off alias of that). Next: analyze_project_gaps.")]
    public async Task<ToolResult> ValidateProject(
        [Description("Absolute path to the UiPath project directory.")] string projectPath,
        [Description("Run validate (project diagnostics)?")] bool validate = true,
        [Description("Run build (compile gate)? Default false. Pass true for an authoritative CLI compile.")] bool build = false,
        [Description("Run pack?")] bool pack = false,
        CancellationToken cancellationToken = default) {

        var sw = Stopwatch.StartNew();

        if (ToolResults.GuardProject(_filesystem, projectPath, sw) is { } guardFailure) {
            return guardFailure;
        }

        var cliResult = await _cliProvider.ValidateAsync(projectPath, validate, build, pack, cancellationToken);
        var diagnostics = ProjectDiagnostics(projectPath, cliResult);
        var (boundaryErrors, boundaryWarning) = await BoundaryErrors(projectPath, cancellationToken);
        var errors = cliResult.Errors.Concat(boundaryErrors.Select(e => $"{e.ErrorCode}: {e.Message} Fix: {e.FixHint}")).ToList();
        var success = cliResult.Success && boundaryErrors.Count == 0;
        var summary = !cliResult.Success
            ? cliResult.Summary
            : boundaryErrors.Count > 0
                ? $"{boundaryErrors.Count} coded/XAML boundary violation(s) found."
                : cliResult.Summary;
        var warnings = cliResult.Warnings.ToList();
        if (boundaryWarning is not null) {
            warnings.Add(boundaryWarning);
        }

        return new ToolResult {
            Status = success ? "success" : "error",
            Summary = summary,
            Data = new {
                success,
                validate = StepData(cliResult.Validate),
                build = StepData(cliResult.Build),
                pack = StepData(cliResult.Pack),
                errors,
                warnings,
                diagnostics,
                boundary = boundaryErrors,
                recommendations = BuildRecommendations(cliResult, diagnostics, boundaryErrors)
            },
            Errors = errors,
            ErrorDetails = boundaryErrors,
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        };
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

    private async Task<(List<ToolError> Errors, string? Warning)> BoundaryErrors(string projectPath, CancellationToken cancellationToken) {
        if (_modelBuilder is null) {
            return ([], null);
        }

        try {
            var model = await _modelBuilder.BuildAsync(projectPath, cancellationToken);
            var errors = XamlCodedInvokeBoundary.Lint(model)
                .Select(g => new ToolError(
                    ToolErrorCodes.XamlCodedBoundary,
                    g.Message,
                    g.SuggestedAction ?? string.Empty,
                    g.SuggestedTool))
                .ToList();
            return (errors, null);
        } catch (Exception ex) {
            return ([], $"Coded/XAML boundary lint was skipped: {ex.GetType().Name}.");
        }
    }

    private static List<string> BuildRecommendations(UiPathCliResult result, List<object> diagnostics, List<ToolError> boundaryErrors) {
        var recommendations = new List<string>();
        AddRecommendation(recommendations, "validate", result.Validate);
        AddRecommendation(recommendations, "build", result.Build);
        AddRecommendation(recommendations, "pack", result.Pack);
        if (diagnostics.Count > 0) {
            recommendations.Add(
                "Apply diagnostics[].specFix to the activity at diagnostics[].activityId (edit_workflow_activity / insert_activities), then re-run validate_project.");
        }

        if (boundaryErrors.Count > 0) {
            recommendations.Add(
                "Fix coded/XAML boundary violations: InvokeWorkflowFile of a .cs workflow may pass BCL and framework types (including Dictionary, IEnumerable, DataTable, and arrays) but not types defined in this automation; never call coded-source methods from XAML.");
        }

        return recommendations;
    }

    private static void AddRecommendation(List<string> recommendations, string stepName, CliStepResult step) {
        if (step.Executed && !step.Success) {
            recommendations.Add($"Review the {stepName} errors, fix the underlying issues, and re-run validation.");
        }
    }
}
