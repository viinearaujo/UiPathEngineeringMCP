using System.ComponentModel;
using System.Diagnostics;
using ModelContextProtocol.Server;
using UiPath.Engineering.Mcp.Core.Abstractions;
using UiPath.Engineering.Mcp.Core.Docs;
using UiPath.Engineering.Mcp.Core.GapAnalysis;
using UiPath.Engineering.Mcp.Core.Models;
using UiPath.Engineering.Mcp.Core.Parsing;
using UiPath.Engineering.Mcp.Core.Planning;

namespace UiPath.Engineering.Mcp.Tools;

[McpServerToolType]
public sealed class AnalyzeProjectGapsTool {
    private readonly IFilesystemProvider _filesystem;
    private readonly IProjectModelBuilder _modelBuilder;
    private readonly ImplementationPlanStore _planStore;
    private readonly ProjectDocsValidator _docsValidator;

    public AnalyzeProjectGapsTool(
        IFilesystemProvider filesystem,
        IProjectModelBuilder modelBuilder,
        ImplementationPlanStore planStore,
        ProjectDocsValidator docsValidator) {
        _filesystem = filesystem;
        _modelBuilder = modelBuilder;
        _planStore = planStore;
        _docsValidator = docsValidator;
    }

    [McpServerTool(
        UseStructuredContent = true,
        Title = "Analyze Project Gaps",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true),
     Description("Deterministic hygiene gaps (entry point, resilience, logging, naming, coded/XAML boundary, docs, plan). Prefer high-confidence errors/warnings. Next: update_plan_task.")]
    public async Task<ToolResult> AnalyzeProjectGaps(
        [Description("Absolute path to the UiPath project directory.")] string projectPath,
        CancellationToken cancellationToken = default) {

        var sw = Stopwatch.StartNew();

        if (ToolResults.GuardProject(_filesystem, projectPath, sw) is { } guardFailure) {
            return guardFailure;
        }

        var model = await _modelBuilder.BuildAsync(projectPath, cancellationToken);
        if (ToolResults.LoadPlanOrFail(_planStore, projectPath, sw, out var plan) is { } planFailure) {
            return planFailure;
        }

        var docsFindings = _docsValidator.Validate(projectPath, model);
        var gaps = ProjectGapAnalyzer.Analyze(model, plan, docsFindings, _filesystem);

        return ToolResults.Ok($"{gaps.Count} gap(s) found.", new {
            gaps,
            counts = new {
                error = gaps.Count(g => g.Severity == Gap.Error),
                warning = gaps.Count(g => g.Severity == Gap.Warning),
                info = gaps.Count(g => g.Severity == Gap.Info)
            },
            confidence = new {
                high = gaps.Count(g => g.Confidence == Gap.ConfidenceHigh),
                medium = gaps.Count(g => g.Confidence == Gap.ConfidenceMedium),
                low = gaps.Count(g => g.Confidence == Gap.ConfidenceLow)
            },
            categories = gaps
                .Select(g => g.Category)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(Gap.CategoryRank)
                .ThenBy(category => category, StringComparer.Ordinal)
                .Select(category => new { category, count = gaps.Count(g => g.Category == category) })
                .ToList(),
            plan = new {
                exists = plan is not null,
                tasksDone = plan?.Tasks.Count(t => t.Status == PlanTask.Done) ?? 0,
                tasksTotal = plan?.Tasks.Count ?? 0
            }
        }, sw);
    }
}
