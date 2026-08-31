using UiPath.Engineering.Mcp.Core.Caching;
using UiPath.Engineering.Mcp.Core.Models;

namespace UiPath.Engineering.Mcp.Core.Parsing;

public static class ProjectAnalysisView {
    public const string DetailSummary = "summary";
    public const string DetailFull = "full";
    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 50;

    public static ProjectAnalysisResult ToResult(
        UiPathProjectModel model,
        string detail,
        int page,
        int pageSize,
        string? workflowFile) {
        var normalized = (detail ?? DetailSummary).Trim().ToLowerInvariant();
        if (normalized is not (DetailSummary or DetailFull)) {
            throw new ArgumentException(
                $"detail must be '{DetailSummary}' or '{DetailFull}'.", nameof(detail));
        }

        var size = Math.Clamp(pageSize <= 0 ? DefaultPageSize : pageSize, 1, MaxPageSize);
        var pageNumber = Math.Max(1, page);
        var warnings = new List<string>();
        ProjectFingerprint.AddStaleWarning(warnings, model.Stale);

        List<WorkflowModel>? workflows = null;
        var truncated = false;
        var resultPage = pageNumber;

        if (!string.IsNullOrWhiteSpace(workflowFile)) {
            var requestedName = Path.GetFileName(workflowFile);
            if (!requestedName.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)) {
                requestedName += ".xaml";
            }

            var match = model.Workflows.FirstOrDefault(w =>
                string.Equals(w.FileName, requestedName, StringComparison.OrdinalIgnoreCase));
            if (match is null) {
                workflows = [];
                warnings.Add($"Workflow '{requestedName}' was not found. Use WorkflowIndex for names.");
            } else {
                workflows = [match];
            }

            resultPage = 1;
        } else if (normalized == DetailFull) {
            var start = (pageNumber - 1) * size;
            if (start >= model.Workflows.Count) {
                workflows = [];
            } else {
                workflows = model.Workflows.Skip(start).Take(size).ToList();
            }

            truncated = start + (workflows?.Count ?? 0) < model.Workflows.Count || start > 0;
        }

        return new ProjectAnalysisResult {
            Detail = normalized,
            Summary = ToSummary(model),
            Workflows = workflows,
            Page = resultPage,
            PageSize = size,
            TotalWorkflows = model.Workflows.Count,
            Truncated = truncated,
            Stale = model.Stale,
            Warnings = warnings
        };
    }

    public static ProjectAnalysisSummary ToSummary(UiPathProjectModel model) => new() {
        ProjectPath = model.ProjectPath,
        ProjectName = model.ProjectName,
        MainWorkflow = model.MainWorkflow,
        EntryPoints = model.EntryPoints,
        TargetFramework = model.TargetFramework,
        Description = model.Description,
        ReadmeSummary = model.ReadmeSummary,
        FolderStructure = model.FolderStructure,
        Counts = new ProjectAnalysisCounts {
            Workflows = model.Workflows.Count,
            CodedWorkflows = model.CodedWorkflows.Count,
            Packages = model.Packages.Count,
            Variables = model.Variables.Count,
            Arguments = model.Arguments.Count,
            InvokeWorkflows = model.InvokeWorkflows.Count,
            Risks = model.Risks.Count
        },
        WorkflowIndex = model.Workflows.Select(w => new WorkflowIndexEntry {
            FileName = w.FileName,
            FilePath = w.FilePath,
            IsMain = w.IsMain,
            HasParseError = w.HasParseError,
            ActivityCount = w.Activities.Count,
            ArgumentCount = w.Arguments.Count
        }).ToList(),
        CodedWorkflowIndex = model.CodedWorkflows.Select(c => new CodedWorkflowIndexEntry {
            FileName = c.FileName,
            FilePath = c.FilePath,
            ClassName = c.ClassName,
            Kind = c.Kind,
            IsCodedWorkflow = c.IsCodedWorkflow,
            HasParseError = c.HasParseError
        }).ToList(),
        Packages = model.Packages,
        Dependencies = model.Dependencies,
        Risks = model.Risks,
        Stale = model.Stale
    };
}
