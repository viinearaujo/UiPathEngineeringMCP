using UiPath.Engineering.Mcp.Core.Models;

namespace UiPath.Engineering.Mcp.Core.Authoring;

public static class SpecValidator {
    // Returns all violations; empty list == valid. path e.g. "children[0].children[0]"
    public static List<ToolError> Validate(ActivitySpec spec) =>
        Validate(spec, ActivityCatalog.Fallback, null);

    public static List<ToolError> Validate(ActivitySpec spec, IActivityCatalog catalog) =>
        Validate(spec, catalog, null);

    /// <summary>
    /// Validates a spec against a catalog and the project's expression language.
    /// The language decides which expression form a property may carry: a
    /// VisualBasic project uses <c>[bracket]</c> shorthand, a CSharp project uses
    /// a raw C# expression (brackets are forbidden there — they deserialize as
    /// VisualBasicValue and break C#-only syntax).
    /// </summary>
    /// <remarks>
    /// A schema with a complete property surface (<see cref="ActivitySchema.PropertiesAreComplete"/>,
    /// set by reflection over the activity's assemblies) rejects a property the
    /// schema does not know about: it is a typo that would otherwise render as a
    /// silent passthrough attribute. A schema whose surface is a curated list or a
    /// discovered sample keeps tolerating unknown properties — those deliberately
    /// under-list, so an unknown key may name a real property of a newer package
    /// version; the builder reports the passthrough as a warning.
    /// </remarks>
    public static List<ToolError> Validate(ActivitySpec spec, IActivityCatalog catalog, ProjectXamlSettings? settings) {
        var errors = new List<ToolError>();
        if (spec is null || string.IsNullOrWhiteSpace(spec.Name)) {
            errors.Add(new ToolError(
                ToolErrorCodes.SpecEmptySpec,
                "The activity spec is empty: 'name' is missing or blank.",
                "Provide a spec with a 'name' matching an activity from the catalog, e.g. { \"name\": \"Sequence\", \"children\": [...] }.",
                "validate_activity_spec"));
            return errors;
        }

        var context = new Context(catalog, settings ?? ProjectXamlSettings.Default);
        Walk(spec, path: spec.Name, isRoot: true, errors, context);
        return errors;
    }

    private static void Walk(ActivitySpec spec, string path, bool isRoot, List<ToolError> errors, Context context) {
        if (!context.Catalog.TryGet(spec.Name, out var schema)) {
            var suggestion = context.Catalog.Suggest(spec.Name);
            var fixHint = suggestion is null
                ? "Pick an activity name from the catalog (case-insensitive), or call recommend_activities."
                : $"Did you mean \"{suggestion}\"? Use a catalog activity name (case-insensitive).";
            errors.Add(new ToolError(
                ToolErrorCodes.SpecUnknownActivity,
                $"Unknown activity \"{spec.Name}\" at {path}.",
                fixHint,
                "recommend_activities"));
            // Skip validating inside the unknown activity itself (no schema to check
            // against); its children are still validated below to avoid cascaded noise.
        } else {
            ValidateAgainstSchema(spec, schema, path, isRoot, errors, context);
        }

        if (spec.Children is not null) {
            for (var i = 0; i < spec.Children.Count; i++) {
                Walk(spec.Children[i], $"{path}.children[{i}]", isRoot: false, errors, context);
            }
        }

        if (spec.Else is not null) {
            for (var i = 0; i < spec.Else.Count; i++) {
                Walk(spec.Else[i], $"{path}.else[{i}]", isRoot: false, errors, context);
            }
        }

        if (spec.Default is not null) {
            for (var i = 0; i < spec.Default.Count; i++) {
                Walk(spec.Default[i], $"{path}.default[{i}]", isRoot: false, errors, context);
            }
        }

        if (spec.Cases is not null) {
            for (var i = 0; i < spec.Cases.Count; i++) {
                var catchChildren = spec.Cases[i].Children;
                if (catchChildren is null) continue;
                for (var j = 0; j < catchChildren.Count; j++) {
                    Walk(catchChildren[j], $"{path}.cases[{i}].children[{j}]", isRoot: false, errors, context);
                }
            }
        }

        if (spec.Catches is not null) {
            for (var i = 0; i < spec.Catches.Count; i++) {
                var catchChildren = spec.Catches[i].Children;
                if (catchChildren is null) continue;
                for (var j = 0; j < catchChildren.Count; j++) {
                    Walk(catchChildren[j], $"{path}.catches[{i}].children[{j}]", isRoot: false, errors, context);
                }
            }
        }
    }

    private static void ValidateAgainstSchema(
        ActivitySpec spec, ActivitySchema schema, string path, bool isRoot, List<ToolError> errors, Context context) {
        var lookup = PropertyLookup(schema);

        foreach (var property in schema.Properties) {
            if (property.Required && (spec.Properties is null || !ContainsProperty(spec.Properties, property.Name))) {
                errors.Add(new ToolError(
                    ToolErrorCodes.SpecMissingRequiredProperty,
                    $"Activity \"{schema.Name}\" at {path} is missing required property \"{property.Name}\".",
                    $"Add \"{property.Name}\" with the correct form: {CorrectForm(property, context)}."));
            }
        }

        if (spec.Properties is not null) {
            foreach (var (name, value) in spec.Properties) {
                if (!lookup.TryGetValue(name, out var property)) {
                    // A complete surface makes an unknown key a typo; an incomplete
                    // one tolerates it for forward compatibility with a newer
                    // activity package, and the builder warns on the passthrough.
                    if (schema.PropertiesAreComplete) {
                        errors.Add(new ToolError(
                            ToolErrorCodes.SpecUnknownProperty,
                            $"Property \"{name}\" of \"{schema.Name}\" at {path} is not a property of this activity.",
                            UnknownPropertyHint(schema, name, context)));
                    }

                    continue;
                }

                var mismatch = FormMismatch(property, value, context);
                if (mismatch is not null) {
                    errors.Add(new ToolError(
                        ToolErrorCodes.SpecValueFormMismatch,
                        $"Property \"{name}\" of \"{schema.Name}\" at {path}: {mismatch}",
                        $"Use the correct form for \"{property.Name}\": {CorrectForm(property, context)}."));
                    continue;
                }

                if (AllowedValueMismatch(property, value) is { } allowed) {
                    errors.Add(new ToolError(
                        ToolErrorCodes.SpecValueFormMismatch,
                        $"Property \"{name}\" of \"{schema.Name}\" at {path}: {allowed}",
                        $"Set \"{property.Name}\" to one of: {string.Join(", ", property.AllowedValues!)}."));
                }
            }
        }

        if (spec.Children is { Count: > 0 } && !schema.IsContainer) {
            errors.Add(new ToolError(
                ToolErrorCodes.SpecInvalidNesting,
                $"Activity \"{schema.Name}\" at {path} is not a container but has {spec.Children.Count} child(ren).",
                BodyHint(schema.Name)));
        }

        // While / DoWhile take exactly one Activity body: a second child has no
        // slot to go into, and the XAML loader rejects it.
        if (spec.Children is { Count: > 1 } && TakesSingleBody(schema)) {
            errors.Add(new ToolError(
                ToolErrorCodes.SpecInvalidNesting,
                $"Activity \"{schema.Name}\" at {path} takes a single Activity body but has {spec.Children.Count} children.",
                $"Wrap the {spec.Children.Count} activities in one Sequence and make that the only child: {{ \"name\": \"Sequence\", \"children\": [...] }}."));
        }

        if (spec.Variables is { Count: > 0 } && !isRoot && !IsSequence(schema)) {
            errors.Add(new ToolError(
                ToolErrorCodes.SpecInvalidNesting,
                $"Activity \"{schema.Name}\" at {path} declares variables; only a Sequence can own a variable scope.",
                "Move the 'variables' list onto this activity's enclosing Sequence, or wrap the activities in a Sequence that declares them."));
        }

        if (spec.Imports is { Count: > 0 } && !isRoot) {
            errors.Add(new ToolError(
                ToolErrorCodes.SpecInvalidNesting,
                $"Activity \"{schema.Name}\" at {path} declares imports, which are only allowed on the root spec.",
                "Move the 'imports' list to the root spec."));
        }

        if (spec.WorkflowArguments is { Count: > 0 } && !isRoot) {
            errors.Add(new ToolError(
                ToolErrorCodes.SpecInvalidNesting,
                $"Activity \"{schema.Name}\" at {path} declares workflowArguments, which are only allowed on the root spec.",
                "Move the 'workflowArguments' list to the root spec."));
        }

        if (spec.WorkflowArguments is not null) {
            for (var i = 0; i < spec.WorkflowArguments.Count; i++) {
                var argument = spec.WorkflowArguments[i];
                if (string.IsNullOrWhiteSpace(argument.Name)) {
                    errors.Add(new ToolError(
                        ToolErrorCodes.SpecMissingRequiredProperty,
                        $"Workflow argument at {path}.workflowArguments[{i}] is missing required property \"name\".",
                        "Set \"name\" to the argument name, e.g. \"in_FilePath\"."));
                    continue;
                }

                if (string.IsNullOrWhiteSpace(argument.Type)) {
                    errors.Add(new ToolError(
                        ToolErrorCodes.SpecMissingRequiredProperty,
                        $"Workflow argument \"{argument.Name}\" at {path}.workflowArguments[{i}] is missing required property \"type\".",
                        "Set \"type\" to the argument type, e.g. \"String\" or \"DataTable\"."));
                }

                if (!IsKnownDirection(argument.Direction)) {
                    errors.Add(new ToolError(
                        ToolErrorCodes.SpecValueFormMismatch,
                        $"Workflow argument \"{argument.Name}\" at {path}.workflowArguments[{i}] has direction \"{argument.Direction}\".",
                        "Use direction \"In\", \"Out\", or \"InOut\"."));
                }
            }
        }

        if (spec.Catches is { Count: > 0 } && schema.Name != "TryCatch") {
            errors.Add(new ToolError(
                ToolErrorCodes.SpecInvalidNesting,
                $"Activity \"{schema.Name}\" at {path} declares catches, which are only allowed on TryCatch.",
                "Move the 'catches' list onto a TryCatch activity."));
        }

        if (spec.Else is { Count: > 0 } && schema.Name != "If") {
            errors.Add(new ToolError(
                ToolErrorCodes.SpecInvalidNesting,
                $"Activity \"{schema.Name}\" at {path} declares else, which is only allowed on If.",
                "Move the 'else' list onto an If activity (Children is the Then branch)."));
        }

        if (spec.Cases is { Count: > 0 } && schema.Name != "Switch") {
            errors.Add(new ToolError(
                ToolErrorCodes.SpecInvalidNesting,
                $"Activity \"{schema.Name}\" at {path} declares cases, which are only allowed on Switch.",
                "Move the 'cases' list onto a Switch activity."));
        }

        if (spec.Default is { Count: > 0 } && schema.Name != "Switch") {
            errors.Add(new ToolError(
                ToolErrorCodes.SpecInvalidNesting,
                $"Activity \"{schema.Name}\" at {path} declares default, which is only allowed on Switch.",
                "Move the 'default' list onto a Switch activity."));
        }

        if (spec.Arguments is { Count: > 0 } && !ActivityCatalog.AcceptsArgumentDictionary(schema.Name)) {
            errors.Add(new ToolError(
                ToolErrorCodes.SpecInvalidNesting,
                $"Activity \"{schema.Name}\" at {path} declares arguments, which are only allowed on {string.Join(" and ", ActivityCatalog.ArgumentDictionaryActivities)}.",
                $"Move the 'arguments' list onto a {string.Join(" or ", ActivityCatalog.ArgumentDictionaryActivities)} activity."));
        }

        if (spec.Flowchart is not null && schema.Name != "Flowchart") {
            errors.Add(new ToolError(
                ToolErrorCodes.SpecInvalidNesting,
                $"Activity \"{schema.Name}\" at {path} declares a flowchart, which is only allowed on Flowchart.",
                "Move the 'flowchart' object onto a Flowchart activity."));
        }

        if (spec.StateMachine is not null && schema.Name != "StateMachine") {
            errors.Add(new ToolError(
                ToolErrorCodes.SpecInvalidNesting,
                $"Activity \"{schema.Name}\" at {path} declares a stateMachine, which is only allowed on StateMachine.",
                "Move the 'stateMachine' object onto a StateMachine activity."));
        }

        if (schema.Name == "Flowchart") {
            if (spec.Flowchart is not null) {
                ValidateFlowchart(spec.Flowchart, path, errors);
            }
        }

        if (schema.Name == "StateMachine") {
            if (spec.StateMachine is not null) {
                ValidateStateMachine(spec.StateMachine, path, errors);
            }
        }

        if (schema.Name == "Switch" && spec.Children is { Count: > 0 }) {
            errors.Add(new ToolError(
                ToolErrorCodes.SpecInvalidNesting,
                $"Switch at {path} uses 'children'; Switch branches belong in 'cases' and 'default'.",
                "Replace 'children' with 'cases': [{ \"key\": \"1\", \"children\": [...] }] and optional 'default'."));
        }

        if (schema.Name == "Switch" && spec.Cases is not null) {
            for (var i = 0; i < spec.Cases.Count; i++) {
                if (string.IsNullOrWhiteSpace(spec.Cases[i].Key)) {
                    errors.Add(new ToolError(
                        ToolErrorCodes.SpecMissingRequiredProperty,
                        $"Switch case at {path}.cases[{i}] is missing required property \"key\".",
                        "Set \"key\" to the literal compared against Expression, e.g. \"1\" or \"Open\"."));
                }
            }
        }

        if (ActivityCatalog.AcceptsArgumentDictionary(schema.Name) && spec.Arguments is not null) {
            for (var i = 0; i < spec.Arguments.Count; i++) {
                var argument = spec.Arguments[i];
                if (string.IsNullOrWhiteSpace(argument.Name)) {
                    errors.Add(new ToolError(
                        ToolErrorCodes.SpecMissingRequiredProperty,
                        $"{schema.Name} argument at {path}.arguments[{i}] is missing required property \"name\".",
                        "Set \"name\" to the variable the code or invoked workflow binds to, e.g. \"in_FilePath\"."));
                }

                if (!IsKnownDirection(argument.Direction)) {
                    errors.Add(new ToolError(
                        ToolErrorCodes.SpecValueFormMismatch,
                        $"{schema.Name} argument \"{argument.Name}\" at {path}.arguments[{i}] has direction \"{argument.Direction}\".",
                        "Use direction \"In\", \"Out\", or \"InOut\"."));
                }

                var formMismatch = ArgumentFormMismatch(argument.Value, context);
                if (formMismatch is not null) {
                    errors.Add(new ToolError(
                        ToolErrorCodes.SpecValueFormMismatch,
                        $"{schema.Name} argument \"{argument.Name}\" at {path}.arguments[{i}]: {formMismatch}",
                        CorrectArgumentForm(context)));
                }
            }
        }
    }

    // An argument binding is an expression in the project language: an Out/InOut
    // binding must be a writable reference (a variable or argument name), never a
    // constructed value.
    private static string? ArgumentFormMismatch(string? value, Context context) {
        if (string.IsNullOrEmpty(value)) {
            return null;
        }

        if (context.Settings.IsCSharp && ExpressionValue.IsBracketWrapped(value)) {
            return $"value \"{value}\" uses VB [bracket] shorthand, which this project does not use.";
        }

        return null;
    }

    private static string CorrectArgumentForm(Context context) => context.Settings.IsCSharp
        ? "pass a raw C# expression with no brackets, e.g. \"value\": \"filePath\" (an Out/InOut binding must be a variable or argument name)."
        : "pass the expression in VB [brackets], e.g. \"value\": \"[filePath]\" (an Out/InOut binding must be a variable or argument name).";

    private static string BodyHint(string activityName) => activityName switch {
        "InvokeCode" => "Remove the children. InvokeCode takes no activity body; bind its parameters with 'arguments' and put the code in 'Code'.",
        "InvokeWorkflowFile" => "Remove the children, or nest them inside a container activity such as Sequence. InvokeWorkflowFile takes no activity body.",
        _ => "Remove the children, or nest them inside a container activity such as Sequence."
    };

    private static bool TakesSingleBody(ActivitySchema schema) =>
        schema.Body?.Shape == BodyShape.Activity;

    // Rule 20 structure-first: every link must resolve to a declared node, and an
    // unreferenced node is an orphan the designer renders but never runs.
    private static void ValidateFlowchart(FlowchartSpec flowchart, string path, List<ToolError> errors) {
        var nodes = flowchart.Nodes ?? [];
        if (nodes.Count == 0) {
            errors.Add(new ToolError(
                ToolErrorCodes.SpecMissingRequiredProperty,
                $"Flowchart at {path} declares no nodes.",
                "Add \"nodes\": [ { \"id\": \"1\", \"type\": \"Step\", \"activity\": { ... } } ] and a \"start\" id."));
            return;
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < nodes.Count; i++) {
            var node = nodes[i];
            if (string.IsNullOrWhiteSpace(node.Id)) {
                errors.Add(new ToolError(
                    ToolErrorCodes.SpecMissingRequiredProperty,
                    $"Flowchart node at {path}.flowchart.nodes[{i}] is missing required property \"id\".",
                    "Set \"id\" to a stable node id, e.g. \"1\"; links address nodes by it."));
                continue;
            }

            if (!ids.Add(node.Id)) {
                errors.Add(new ToolError(
                    ToolErrorCodes.SpecInvalidNesting,
                    $"Flowchart at {path} declares two nodes with id \"{node.Id}\".",
                    "Give every node a unique \"id\"."));
            }
        }

        if (string.IsNullOrWhiteSpace(flowchart.Start)) {
            errors.Add(new ToolError(
                ToolErrorCodes.SpecMissingRequiredProperty,
                $"Flowchart at {path} is missing required property \"start\".",
                "Set \"start\" to the id of the first node."));
        }

        var targets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in nodes) {
            var kind = (node.Type ?? "Step").Trim().ToLowerInvariant();
            switch (kind) {
                case "decision":
                    if (string.IsNullOrWhiteSpace(node.Condition)) {
                        errors.Add(MissingNodeProperty(node, "condition", path));
                    }

                    if (string.IsNullOrWhiteSpace(node.True) && string.IsNullOrWhiteSpace(node.False)) {
                        errors.Add(new ToolError(
                            ToolErrorCodes.SpecMissingRequiredProperty,
                            $"FlowDecision \"{node.Id}\" at {path} has neither a \"true\" nor a \"false\" target.",
                            "Set at least one of \"true\" / \"false\" to a node id."));
                    }

                    Track(targets, node.True);
                    Track(targets, node.False);
                    break;

                case "switch":
                    if (string.IsNullOrWhiteSpace(node.Expression)) {
                        errors.Add(MissingNodeProperty(node, "expression", path));
                    }

                    foreach (var switchCase in node.Cases ?? []) {
                        Track(targets, switchCase.Target);
                    }

                    Track(targets, node.Default);
                    break;

                default:
                    // A step with no "next" ends the flow — a valid terminal node.
                    Track(targets, node.Next);
                    break;
            }
        }

        Track(targets, flowchart.Start);
        CheckLinks(ids, targets, "Flowchart", path, errors);
    }

    private static void ValidateStateMachine(StateMachineSpec machine, string path, List<ToolError> errors) {
        var states = machine.States ?? [];
        if (states.Count == 0) {
            errors.Add(new ToolError(
                ToolErrorCodes.SpecMissingRequiredProperty,
                $"StateMachine at {path} declares no states.",
                "Add \"states\": [ { \"id\": \"1\", \"transitions\": [ { \"to\": \"2\" } ] } ] and a \"start\" id."));
            return;
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < states.Count; i++) {
            var state = states[i];
            if (string.IsNullOrWhiteSpace(state.Id)) {
                errors.Add(new ToolError(
                    ToolErrorCodes.SpecMissingRequiredProperty,
                    $"State at {path}.stateMachine.states[{i}] is missing required property \"id\".",
                    "Set \"id\" to a stable state id, e.g. \"1\"."));
                continue;
            }

            if (!ids.Add(state.Id)) {
                errors.Add(new ToolError(
                    ToolErrorCodes.SpecInvalidNesting,
                    $"StateMachine at {path} declares two states with id \"{state.Id}\".",
                    "Give every state a unique \"id\"."));
            }
        }

        if (string.IsNullOrWhiteSpace(machine.Start)) {
            errors.Add(new ToolError(
                ToolErrorCodes.SpecMissingRequiredProperty,
                $"StateMachine at {path} is missing required property \"start\".",
                "Set \"start\" to the id of the initial state."));
        }

        var targets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var state in states) {
            for (var j = 0; j < (state.Transitions?.Count ?? 0); j++) {
                var transition = state.Transitions![j];
                if (string.IsNullOrWhiteSpace(transition.To)) {
                    errors.Add(new ToolError(
                        ToolErrorCodes.SpecMissingRequiredProperty,
                        $"Transition at {path}.stateMachine.states[{state.Id}].transitions[{j}] is missing required property \"to\".",
                        "Set \"to\" to the id of the destination state."));
                    continue;
                }

                Track(targets, transition.To);
            }
        }

        Track(targets, machine.Start);
        CheckLinks(ids, targets, "StateMachine", path, errors);
    }

    private static ToolError MissingNodeProperty(FlowNodeSpec node, string property, string path) => new(
        ToolErrorCodes.SpecMissingRequiredProperty,
        $"Flowchart node \"{node.Id}\" at {path} is missing required property \"{property}\".",
        $"Set \"{property}\" on the node (a {node.Type} needs it).");

    private static void Track(HashSet<string> targets, string? id) {
        if (!string.IsNullOrWhiteSpace(id)) {
            targets.Add(id.Trim());
        }
    }

    // A link to an undeclared node is a file Studio cannot load; an unreferenced
    // node is an orphan the designer draws but nothing ever runs.
    private static void CheckLinks(
        HashSet<string> ids, HashSet<string> targets, string kind, string path, List<ToolError> errors) {
        foreach (var target in targets) {
            if (!ids.Contains(target)) {
                errors.Add(new ToolError(
                    ToolErrorCodes.SpecInvalidNesting,
                    $"{kind} at {path} links to node id \"{target}\", which is not declared.",
                    "Every start/next/true/false/to target must name a declared node id."));
            }
        }

        foreach (var id in ids) {
            if (!targets.Contains(id)) {
                errors.Add(new ToolError(
                    ToolErrorCodes.SpecInvalidNesting,
                    $"{kind} at {path} declares node \"{id}\", but no other node or the start links to it, so it is an orphan.",
                    "Wire it with a start/next/true/false/to reference, or remove it."));
            }
        }
    }

    // A Sequence owns a <Sequence.Variables> block wherever it sits, so a nested
    // Sequence may scope variables to itself.
    private static bool IsSequence(ActivitySchema schema) =>
        string.Equals(schema.RenderName, "Sequence", StringComparison.OrdinalIgnoreCase);

    internal static bool IsKnownDirection(string? direction) {
        if (string.IsNullOrWhiteSpace(direction)) {
            return true; // defaults to In at render time
        }

        return direction.Equals("In", StringComparison.OrdinalIgnoreCase)
            || direction.Equals("Out", StringComparison.OrdinalIgnoreCase)
            || direction.Equals("InOut", StringComparison.OrdinalIgnoreCase)
            || direction.Equals("In/Out", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsProperty(Dictionary<string, string> properties, string name) =>
        properties.ContainsKey(name) || properties.Keys.Any(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase));

    // The closest declared property, so a typo points at the intended name.
    private static string UnknownPropertyHint(ActivitySchema schema, string name, Context context) {
        var known = schema.Properties.Select(p => p.Name).ToList();
        var suggestion = Suggest(name, known);
        if (suggestion is not null) {
            var property = schema.Properties.First(p => p.Name == suggestion);
            return $"Did you mean \"{suggestion}\"? {CorrectForm(property, context)}";
        }

        return known.Count == 0
            ? "This activity declares no settable properties; remove the property bag."
            : $"Remove the property, or use one of: {string.Join(", ", known)}.";
    }

    // Levenshtein under a small threshold, matching ActivityCatalog.Suggest.
    private static string? Suggest(string name, IReadOnlyList<string> candidates) {
        string? best = null;
        var bestDistance = 4;
        foreach (var candidate in candidates) {
            var distance = Levenshtein(name, candidate);
            if (distance < bestDistance) {
                bestDistance = distance;
                best = candidate;
            }
        }

        return best;
    }

    private static int Levenshtein(string a, string b) {
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;

        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) {
            previous[j] = j;
        }

        for (var i = 1; i <= a.Length; i++) {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++) {
                var cost = char.ToUpperInvariant(a[i - 1]) == char.ToUpperInvariant(b[j - 1]) ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }

    private static string? AllowedValueMismatch(PropertySchema property, string value) {
        if (property.AllowedValues is not { Count: > 0 } allowed) {
            return null;
        }

        // A literal property holding an expression is already reported as a form
        // mismatch; do not pile a second error on the same value.
        var candidate = value.Trim();
        return allowed.Any(a => string.Equals(a, candidate, StringComparison.OrdinalIgnoreCase))
            ? null
            : $"value \"{value}\" is not one of the allowed values.";
    }

    private static string? FormMismatch(PropertySchema property, string value, Context context) {
        var wrapped = ExpressionValue.IsBracketWrapped(value);
        // The bracket shorthand is VB-only: in a C# project it deserializes as a
        // VisualBasicValue, so C#-only syntax (null, ?., ??, typeof) breaks.
        if (context.Settings.IsCSharp && property.Kind != PropertyKind.Literal && wrapped) {
            return $"value \"{value}\" uses VB [bracket] shorthand, which this C#-expression project does not use.";
        }

        return property.Kind switch {
            PropertyKind.Expression when !context.Settings.IsCSharp && !wrapped && !ExpressionValue.IsQuotedStringLiteral(value) =>
                $"value \"{value}\" is not expression-wrapped.",
            PropertyKind.Literal when wrapped =>
                $"value \"{value}\" is expression-wrapped but the property is a literal.",
            PropertyKind.TypeArgument when value.Contains('[') || value.Contains(']') =>
                $"value \"{value}\" contains brackets but the property is a type name.",
            _ => null,
        };
    }

    private static string CorrectForm(PropertySchema property, Context context) => property.Kind switch {
        PropertyKind.Expression when context.Settings.IsCSharp =>
            $"pass a raw C# expression with no brackets, e.g. \"{property.Name}\": \"{CSharpExample(property.Name)}\" (a quoted C# string literal like \"\\\"text\\\"\" is also accepted).",
        PropertyKind.Expression =>
            $"wrap a VB expression in brackets, e.g. \"{property.Name}\": \"[myVar + 1]\" (a quoted VB string literal like \"\\\"text\\\"\" is also accepted).",
        PropertyKind.Literal =>
            $"pass the raw value with no brackets, e.g. \"{property.Name}\": \"Info\".",
        PropertyKind.TypeArgument =>
            $"pass a type name with no brackets, e.g. \"{property.Name}\": \"DataRow\".",
        _ => property.Name,
    };

    private static string CSharpExample(string propertyName) => propertyName.ToLowerInvariant() switch {
        "to" => "myVar",
        "condition" => "count > 0",
        _ => "myVar + 1"
    };

    // Case-insensitive property lookup per schema, built once per schema instance.
    private static readonly Dictionary<ActivitySchema, IReadOnlyDictionary<string, PropertySchema>> Lookups = new();

    private static IReadOnlyDictionary<string, PropertySchema> PropertyLookup(ActivitySchema schema) {
        lock (Lookups) {
            if (!Lookups.TryGetValue(schema, out var lookup)) {
                lookup = schema.Properties.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
                Lookups[schema] = lookup;
            }

            return lookup;
        }
    }

    private sealed record Context(IActivityCatalog Catalog, ProjectXamlSettings Settings);
}
