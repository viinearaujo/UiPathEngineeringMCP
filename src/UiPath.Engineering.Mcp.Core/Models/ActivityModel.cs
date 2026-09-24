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
    /// <summary>
    /// The node's <c>x:Name</c> (<c>__ReferenceIDn</c> on a Flowchart /
    /// StateMachine node). Null for a node in a plain child list.
    /// </summary>
    public string? NodeName { get; init; }
    /// <summary>
    /// Outgoing graph links on a Flowchart / StateMachine node: the property
    /// element that holds the link (<c>FlowStep.Next</c>, <c>FlowDecision.True</c>,
    /// <c>Transition.To</c>, …) mapped to the referenced node's <c>x:Name</c>.
    /// Populated only where the file wires nodes by <c>&lt;x:Reference&gt;</c>, so
    /// the graph shape survives the read instead of being flattened away.
    /// </summary>
    public IReadOnlyDictionary<string, string> GraphLinks { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
    public int Depth { get; init; }
    public int Order { get; init; }
    public int Line { get; init; }
    [JsonIgnore]
    public List<ActivityModel> Children { get; init; } = [];
}
