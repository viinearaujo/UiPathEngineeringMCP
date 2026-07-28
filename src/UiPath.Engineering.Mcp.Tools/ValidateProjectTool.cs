using System.Diagnostics;
using ModelContextProtocol.Server;
using UiPath.Engineering.Mcp.Core.Models;
using UiPath.Engineering.Mcp.Core.Abstractions;
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

    [McpServerTool, Description("Validates a UiPath project using the UiPath CLI and returns structured restore, analyze, pack, error, and warning data.")]
    public async Task<ToolResult> ValidateProject(
        [Description("Absolute path to the UiPath project directory.")] string projectPath,
        [Description("Run restore?")] bool restore = true,
        [Description("Run analyze?")] bool analyze = true,
        [Description("Run pack?")] bool pack = false) {
        
        var sw = Stopwatch.StartNew();

        if (!_filesystem.IsPathAllowed(projectPath)) {
            return new ToolResult { Status = "error", Summary = "Path not allowed.", Errors = ["Path outside allowed roots."], DurationMs = sw.ElapsedMilliseconds };
        }

        if (_filesystem.FindProjectJson(projectPath) == null) {
            return new ToolResult { Status = "error", Summary = "project.json not found.", Errors = ["Invalid UiPath project directory."], DurationMs = sw.ElapsedMilliseconds };
        }

        var cliResult = await _cliProvider.ValidateAsync(projectPath, restore, analyze, pack);

        return new ToolResult {
            Status = cliResult.Success ? "success" : "error",
            Summary = cliResult.Summary,
            Data = new {
                success = cliResult.Success,
                restore = StepData(cliResult.Restore),
                analyze = StepData(cliResult.Analyze),
                pack = StepData(cliResult.Pack),
                errors = cliResult.Errors,
                warnings = cliResult.Warnings,
                recommendations = BuildRecommendations(cliResult)
            },
            Errors = cliResult.Errors,
            Warnings = cliResult.Warnings,
            DurationMs = sw.ElapsedMilliseconds
        };
    }

    private static object StepData(CliStepResult step) => new {
        executed = step.Executed,
        success = step.Executed && step.Success,
        errors = step.Errors,
        warnings = step.Warnings
    };

    private static List<string> BuildRecommendations(UiPathCliResult result) {
        var recommendations = new List<string>();
        AddRecommendation(recommendations, "restore", result.Restore);
        AddRecommendation(recommendations, "analyze", result.Analyze);
        AddRecommendation(recommendations, "pack", result.Pack);
        return recommendations;
    }

    private static void AddRecommendation(List<string> recommendations, string stepName, CliStepResult step) {
        if (step.Executed && !step.Success) {
            recommendations.Add($"Review the {stepName} errors, fix the underlying issues, and re-run validation.");
        }
    }
}