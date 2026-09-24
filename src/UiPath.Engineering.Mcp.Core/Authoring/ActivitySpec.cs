namespace UiPath.Engineering.Mcp.Core.Authoring;

public sealed class ActivitySpec {
    public string Name { get; set; } = string.Empty;
    public Dictionary<string, string>? Properties { get; set; }
    public List<ActivitySpec>? Children { get; set; }
    public List<VariableSpec>? Variables { get; set; }   // allowed on the root spec only
    public List<CatchSpec>? Catches { get; set; }        // TryCatch only
    public List<ActivitySpec>? Else { get; set; }        // If only — Else branch; Children is Then
    public List<SwitchCaseSpec>? Cases { get; set; }     // Switch only
    public List<ActivitySpec>? Default { get; set; }     // Switch only — default branch
    public List<ArgumentMappingSpec>? Arguments { get; set; } // InvokeWorkflowFile and InvokeCode only
    public List<string>? Imports { get; set; }           // root spec only — expression namespaces

    /// <summary>
    /// Studio annotation text, rendered as
    /// <c>sap2010:Annotation.AnnotationText</c>. Studio shows it as the node's
    /// description in the designer and the Outline pane.
    /// </summary>
    public string? Annotation { get; set; }

    // The workflow's own argument declarations, rendered as <x:Property> children
    // of the root <x:Members>. Root spec only. Distinct from Arguments, which are
    // the In/Out bindings of a single InvokeWorkflowFile / InvokeCode call.
    public List<ArgumentSpec>? WorkflowArguments { get; set; }

    // Flowchart / StateMachine graphs. Flowchart and StateMachine carry their
    // nodes here rather than in Children, because their nodes are wired by
    // x:Reference, not by nesting — see FlowchartSpec/StateMachineSpec.
    public FlowchartSpec? Flowchart { get; set; }
    public StateMachineSpec? StateMachine { get; set; }
}

/// <summary>
/// A Flowchart graph. Nodes are rendered as direct children of
/// <c>&lt;Flowchart&gt;</c> (Rule 20 structure-first) and wired through
/// <c>Flowchart.StartNode</c> / <c>FlowStep.Next</c> /
/// <c>FlowDecision.True|False</c> using <c>&lt;x:Reference&gt;</c>. Each node
/// carries an explicit <see cref="FlowNodeSpec.Id"/> that the wiring and the
/// references address.
/// </summary>
public sealed class FlowchartSpec {
    public string? Start { get; set; }
    public List<FlowNodeSpec>? Nodes { get; set; }
}

/// <summary>One Flowchart node: a FlowStep, FlowDecision, or FlowSwitch.</summary>
public sealed class FlowNodeSpec {
    /// <summary>Stable node id, referenced by Start / Next / True / False.</summary>
    public string Id { get; set; } = "";

    /// <summary>Step (default), Decision, or Switch.</summary>
    public string Type { get; set; } = "Step";

    public string? DisplayName { get; set; }

    // Step
    public string? Next { get; set; }
    public ActivitySpec? Activity { get; set; }

    // Decision
    public string? Condition { get; set; }
    public string? True { get; set; }
    public string? False { get; set; }

    // Switch
    public string? Expression { get; set; }
    public string? TypeArgument { get; set; }
    public List<FlowSwitchCaseSpec>? Cases { get; set; }
    public string? Default { get; set; }

    // ViewState — mandatory on every node (Rule 20).
    public string? ShapeLocation { get; set; }
    public string? ShapeSize { get; set; }
}

public sealed class FlowSwitchCaseSpec {
    public string Key { get; set; } = "";
    public string? Target { get; set; }
}

/// <summary>
/// A StateMachine graph. States are direct children of
/// <c>&lt;StateMachine&gt;</c>; transitions live inside each state and point at
/// their destination with <c>&lt;x:Reference&gt;</c>.
/// </summary>
public sealed class StateMachineSpec {
    public string? Start { get; set; }
    public List<StateSpec>? States { get; set; }
}

public sealed class StateSpec {
    public string Id { get; set; } = "";
    public string? DisplayName { get; set; }
    public List<StateTransitionSpec>? Transitions { get; set; }
    public List<ActivitySpec>? Activities { get; set; }

    /// <summary>State type kind: "State" (default) or "FinalState".</summary>
    public string? Kind { get; set; }

    // ViewState — mandatory on every state (Rule 20).
    public string? ShapeLocation { get; set; }
    public string? ShapeSize { get; set; }
}

public sealed class StateTransitionSpec {
    public string? DisplayName { get; set; }
    public string? To { get; set; }
    public string? Condition { get; set; }
    public string? ShapeLocation { get; set; }
    public string? ShapeSize { get; set; }

    /// <summary>Transition kind: "Transition" (default) or "Transition`1".</summary>
    public string? TypeArgument { get; set; }
}

public sealed class VariableSpec {
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public string? Default { get; set; }
}

public sealed class CatchSpec {
    public string Exception { get; set; } = "System.Exception";
    public List<ActivitySpec>? Children { get; set; }
}

public sealed class SwitchCaseSpec {
    public string Key { get; set; } = "";
    public List<ActivitySpec>? Children { get; set; }
}

public sealed class ArgumentMappingSpec {
    public string Name { get; set; } = "";
    public string Direction { get; set; } = "In"; // In, Out, InOut / In/Out
    public string Type { get; set; } = "String";
    public string? Value { get; set; }
}

// One workflow argument declaration: an <x:Property> member of the root
// <x:Members>. Rendered as Type="InArgument(x:String)" (or Out/InOut).
public sealed class ArgumentSpec {
    public string Name { get; set; } = "";
    public string Type { get; set; } = "String";
    public string Direction { get; set; } = "In"; // In, Out, InOut / In/Out
}
