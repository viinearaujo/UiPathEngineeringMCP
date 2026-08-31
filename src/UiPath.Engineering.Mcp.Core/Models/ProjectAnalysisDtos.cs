namespace UiPath.Engineering.Mcp.Core.Models;

public sealed class ProjectAnalysisResult {
    public string Detail { get; init; } = string.Empty;
    public ProjectAnalysisSummary Summary { get; init; } = new();
    public List<WorkflowModel>? Workflows { get; init; }
    public int Page { get; init; }
    public int PageSize { get; init; }
    public int TotalWorkflows { get; init; }
    public bool Truncated { get; init; }
    public List<string> Warnings { get; init; } = [];
}

public sealed class ProjectAnalysisSummary {
    public string ProjectPath { get; init; } = string.Empty;
    public string ProjectName { get; init; } = string.Empty;
    public string? MainWorkflow { get; init; }
    public List<string> EntryPoints { get; init; } = [];
    public string? TargetFramework { get; init; }
    public string? Description { get; init; }
    public string? ReadmeSummary { get; init; }
    public DirectoryTreeNode? FolderStructure { get; init; }
    public ProjectAnalysisCounts Counts { get; init; } = new();
    public List<WorkflowIndexEntry> WorkflowIndex { get; init; } = [];
    public List<CodedWorkflowIndexEntry> CodedWorkflowIndex { get; init; } = [];
    public List<PackageModel> Packages { get; init; } = [];
    public List<string> Dependencies { get; init; } = [];
    public List<string> Risks { get; init; } = [];
}

public sealed class ProjectAnalysisCounts {
    public int Workflows { get; init; }
    public int CodedWorkflows { get; init; }
    public int Packages { get; init; }
    public int Variables { get; init; }
    public int Arguments { get; init; }
    public int InvokeWorkflows { get; init; }
    public int Risks { get; init; }
}

public sealed class WorkflowIndexEntry {
    public string FileName { get; init; } = string.Empty;
    public string FilePath { get; init; } = string.Empty;
    public bool IsMain { get; init; }
    public bool HasParseError { get; init; }
    public int ActivityCount { get; init; }
    public int ArgumentCount { get; init; }
}

public sealed class CodedWorkflowIndexEntry {
    public string FileName { get; init; } = string.Empty;
    public string FilePath { get; init; } = string.Empty;
    public string ClassName { get; init; } = string.Empty;
    public string Kind { get; init; } = CodedFileKind.Source;
    public bool IsCodedWorkflow { get; init; }
    public bool HasParseError { get; init; }
}
