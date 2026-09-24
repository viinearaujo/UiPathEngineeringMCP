using System.Text.Json.Serialization;

namespace UiPath.Engineering.Mcp.Core.Models;

public sealed class ActivityModel {
    public string Id { get; init; } = string.Empty;
    /// <summary>Studio <c>WorkflowViewState.IdRef</c> when present; stable across structural inserts.</summary>
    public string? IdRef { get; init; }
    public string? ParentId { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty;
    /// <summary>Studio annotation (<c>sap2010:Annotation.AnnotationText</c>) on this activity, if any.</summary>
    public string? Annotation { get; init; }
    /// <summary>
    /// The attached-property slot this activity sits in when its parent is a
    /// branch container (Then/Else, Try/Catch/Finally, Body, Default, a Switch
    /// case). Null for a plain child list. Lets Rule 24 wrap detection tell a
    /// wrapped branch from a bare one without changing the structural-path ID.
    /// </summary>
    public string? Slot { get; init; }
    public int Depth { get; init; }
    public int Order { get; init; }
    public int Line { get; init; }
    [JsonIgnore]
    public List<ActivityModel> Children { get; init; } = [];
}
