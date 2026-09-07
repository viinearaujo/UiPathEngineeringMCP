using UiPath.Engineering.Mcp.Core.Models;

namespace UiPath.Engineering.Mcp.Core.Parsing;

/// <summary>
/// Project-relative workflow identity: <c>/</c>-normalized paths, invoke-target
/// resolution, and lookup by relative path or unique file name.
/// </summary>
public static class WorkflowPath {
    public static string ToRelativePath(string projectPath, string filePath) {
        try {
            var relative = Path.GetRelativePath(projectPath, filePath);
            if (string.IsNullOrWhiteSpace(relative)
                || relative.StartsWith("..", StringComparison.Ordinal)
                || Path.IsPathRooted(relative)) {
                return ProjectFilePolicy.NormalizeRelativePath(Path.GetFileName(filePath) ?? filePath);
            }

            return ProjectFilePolicy.NormalizeRelativePath(relative);
        } catch (ArgumentException) {
            return ProjectFilePolicy.NormalizeRelativePath(Path.GetFileName(filePath) ?? filePath);
        }
    }

    public static string Identity(WorkflowModel workflow) =>
        string.IsNullOrWhiteSpace(workflow.RelativePath) ? workflow.FileName : workflow.RelativePath;

    public static string NormalizeRef(string? raw) {
        if (string.IsNullOrWhiteSpace(raw)) {
            return string.Empty;
        }

        return ProjectFilePolicy.NormalizeRelativePath(raw.Trim().Trim('"'));
    }

    public static bool IsMain(WorkflowModel workflow, string? mainWorkflow) {
        if (string.IsNullOrWhiteSpace(mainWorkflow)) {
            return false;
        }

        var requested = NormalizeRef(mainWorkflow);
        return Matches(workflow, requested);
    }

    public static bool Matches(WorkflowModel workflow, string requested) {
        var normalized = NormalizeRef(requested);
        if (normalized.Length == 0) {
            return false;
        }

        if (!normalized.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)
            && !normalized.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) {
            normalized += ".xaml";
        }

        if (string.Equals(Identity(workflow), normalized, StringComparison.OrdinalIgnoreCase)) {
            return true;
        }

        if (string.Equals(workflow.FileName, normalized, StringComparison.OrdinalIgnoreCase)
            || string.Equals(workflow.FileName, Path.GetFileName(normalized), StringComparison.OrdinalIgnoreCase)) {
            return true;
        }

        var filePath = ProjectFilePolicy.NormalizeRelativePath(workflow.FilePath.Replace('\\', '/'));
        return filePath.EndsWith("/" + normalized, StringComparison.OrdinalIgnoreCase)
            || string.Equals(filePath, normalized, StringComparison.OrdinalIgnoreCase);
    }

    public static WorkflowModel? Find(
        IEnumerable<WorkflowModel> workflows,
        string requested,
        List<string>? warnings = null) {
        var normalized = NormalizeRef(requested);
        if (normalized.Length == 0) {
            return null;
        }

        if (!normalized.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)
            && !normalized.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) {
            normalized += ".xaml";
        }

        var list = workflows as IReadOnlyList<WorkflowModel> ?? workflows.ToList();
        var exact = list.Where(w =>
            string.Equals(Identity(w), normalized, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count == 1) {
            return exact[0];
        }

        if (exact.Count > 1) {
            warnings?.Add($"Workflow '{normalized}' is ambiguous; pass a project-relative path.");
            return null;
        }

        var fileName = Path.GetFileName(normalized);
        var byName = list.Where(w =>
            string.Equals(w.FileName, fileName, StringComparison.OrdinalIgnoreCase)).ToList();
        if (byName.Count == 1) {
            if (normalized.Contains('/', StringComparison.Ordinal)
                && !string.Equals(Identity(byName[0]), normalized, StringComparison.OrdinalIgnoreCase)) {
                warnings?.Add($"Workflow '{normalized}' was not found; matched '{Identity(byName[0])}' by file name.");
            }

            return byName[0];
        }

        if (byName.Count > 1) {
            warnings?.Add($"Workflow '{fileName}' matches {byName.Count} files; pass a project-relative path from WorkflowIndex.");
        }

        return null;
    }
}
