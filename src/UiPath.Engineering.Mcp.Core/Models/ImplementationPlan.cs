namespace UiPath.Engineering.Mcp.Core.Models;

public sealed class PlanTask {
    public const string Pending = "pending";
    public const string InProgress = "in_progress";
    public const string Done = "done";
    public const string Blocked = "blocked";

    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Status { get; set; } = Pending;
    public List<string> TargetFiles { get; set; } = [];
    public List<string> AcceptanceCriteria { get; set; } = [];
    public string? Notes { get; set; }
}

public sealed class ImplementationPlan {
    public string Goal { get; set; } = string.Empty;
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset UpdatedUtc { get; set; }
    public List<PlanTask> Tasks { get; set; } = [];
}
