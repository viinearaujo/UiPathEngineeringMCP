using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using UiPath.Engineering.Mcp.Core.Abstractions;
using UiPath.Engineering.Mcp.Core.Models;

namespace UiPath.Engineering.Mcp.Core.Planning;

/// <summary>
/// Persists an <see cref="ImplementationPlan"/> inside the target UiPath project as
/// docs/implementation-plan.json (source of truth) plus a Markdown mirror that is
/// regenerated on every save.
/// </summary>
public sealed class ImplementationPlanStore {
    public const string PlanDirectoryName = "docs";
    public const string PlanJsonFileName = "implementation-plan.json";
    public const string PlanMarkdownFileName = "implementation-plan.md";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _saveLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly IFilesystemProvider _filesystem;

    public ImplementationPlanStore(IFilesystemProvider filesystem) => _filesystem = filesystem;

    public static string GetJsonPath(string projectPath) =>
        Path.Combine(projectPath, PlanDirectoryName, PlanJsonFileName);

    public static string GetMarkdownPath(string projectPath) =>
        Path.Combine(projectPath, PlanDirectoryName, PlanMarkdownFileName);

    public bool Exists(string projectPath) => _filesystem.FileExists(GetJsonPath(projectPath));

    public ImplementationPlan? Load(string projectPath) {
        var jsonPath = GetJsonPath(projectPath);
        if (!_filesystem.FileExists(jsonPath)) {
            return null;
        }

        return JsonSerializer.Deserialize<ImplementationPlan>(_filesystem.ReadAllText(jsonPath));
    }

    public void Save(string projectPath, ImplementationPlan plan) {
        plan.UpdatedUtc = DateTimeOffset.UtcNow;

        var key = Path.GetFullPath(projectPath);
        var gate = _saveLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        gate.Wait();
        try {
            _filesystem.CreateDirectory(Path.Combine(projectPath, PlanDirectoryName));
            _filesystem.WriteAllText(GetJsonPath(projectPath), JsonSerializer.Serialize(plan, JsonOptions));
            _filesystem.WriteAllText(GetMarkdownPath(projectPath), RenderMarkdown(plan));
        } finally {
            gate.Release();
        }
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
