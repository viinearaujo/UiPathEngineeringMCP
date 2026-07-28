using System.Text;
using System.Text.Json;
using UiPath.Engineering.Mcp.Core.Models;

namespace UiPath.Engineering.Mcp.Core.Planning;

/// <summary>
/// Persists an <see cref="ImplementationPlan"/> inside the target UiPath project as
/// docs/implementation-plan.json (source of truth) plus a Markdown mirror that is
/// regenerated on every save. Plain BCL filesystem, like the authoring tools.
/// </summary>
public sealed class ImplementationPlanStore {
    public const string PlanDirectoryName = "docs";
    public const string PlanJsonFileName = "implementation-plan.json";
    public const string PlanMarkdownFileName = "implementation-plan.md";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string GetJsonPath(string projectPath) =>
        Path.Combine(projectPath, PlanDirectoryName, PlanJsonFileName);

    public static string GetMarkdownPath(string projectPath) =>
        Path.Combine(projectPath, PlanDirectoryName, PlanMarkdownFileName);

    public bool Exists(string projectPath) => File.Exists(GetJsonPath(projectPath));

    public ImplementationPlan? Load(string projectPath) {
        var jsonPath = GetJsonPath(projectPath);
        if (!File.Exists(jsonPath)) {
            return null;
        }

        return JsonSerializer.Deserialize<ImplementationPlan>(File.ReadAllText(jsonPath));
    }

    public void Save(string projectPath, ImplementationPlan plan) {
        plan.UpdatedUtc = DateTimeOffset.UtcNow;

        Directory.CreateDirectory(Path.Combine(projectPath, PlanDirectoryName));
        File.WriteAllText(GetJsonPath(projectPath), JsonSerializer.Serialize(plan, JsonOptions));
        File.WriteAllText(GetMarkdownPath(projectPath), RenderMarkdown(plan));
    }

    private static string RenderMarkdown(ImplementationPlan plan) {
        var sb = new StringBuilder();
        sb.AppendLine("# Implementation Plan");
        sb.AppendLine();
        sb.AppendLine($"**Goal:** {plan.Goal}");
        sb.AppendLine();
        sb.AppendLine($"Created: {plan.CreatedUtc:yyyy-MM-dd HH:mm} UTC | Updated: {plan.UpdatedUtc:yyyy-MM-dd HH:mm} UTC");
        sb.AppendLine();

        foreach (var task in plan.Tasks) {
            sb.AppendLine($"## {task.Id}: {task.Title}");
            sb.AppendLine();
            sb.AppendLine($"- Status: {task.Status}");
            if (!string.IsNullOrWhiteSpace(task.Description)) {
                sb.AppendLine($"- Description: {task.Description}");
            }
            if (task.TargetFiles.Count > 0) {
                sb.AppendLine($"- Target files: {string.Join(", ", task.TargetFiles)}");
            }
            if (task.AcceptanceCriteria.Count > 0) {
                sb.AppendLine("- Acceptance criteria:");
                foreach (var criterion in task.AcceptanceCriteria) {
                    sb.AppendLine($"  - {criterion}");
                }
            }
            if (!string.IsNullOrWhiteSpace(task.Notes)) {
                sb.AppendLine($"- Notes: {task.Notes}");
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }
}
