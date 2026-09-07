using System.Text.Json.Serialization;

namespace UiPath.Engineering.Mcp.Core.Models;

public sealed class ActivityModel {
    public string Id { get; init; } = string.Empty;
    /// <summary>Studio <c>WorkflowViewState.IdRef</c> when present; stable across structural inserts.</summary>
    public string? IdRef { get; init; }
    public string? ParentId { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty;
    public int Depth { get; init; }
    public int Order { get; init; }
    public int Line { get; init; }
    [JsonIgnore]
    public List<ActivityModel> Children { get; init; } = [];
}
