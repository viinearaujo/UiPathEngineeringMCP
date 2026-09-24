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

    // The workflow's own argument declarations, rendered as <x:Property> children
    // of the root <x:Members>. Root spec only. Distinct from Arguments, which are
    // the In/Out bindings of a single InvokeWorkflowFile / InvokeCode call.
    public List<ArgumentSpec>? WorkflowArguments { get; set; }
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
