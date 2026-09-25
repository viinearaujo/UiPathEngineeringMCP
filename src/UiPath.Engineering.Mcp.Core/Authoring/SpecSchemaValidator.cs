using UiPath.Engineering.Mcp.Core.Models;

namespace UiPath.Engineering.Mcp.Core.Authoring;

/// <summary>
/// Validates a spec against a catalog schema: required properties, unknown keys,
/// nesting rules, and argument-dictionary shape.
/// </summary>
public static class SpecSchemaValidator {
    public static void ValidateAgainstSchema(
        ActivitySpec spec, ActivitySchema schema, string path, bool isRoot, List<ToolError> errors, SpecValidationContext context) {
        var lookup = SpecValidator.PropertyLookup(schema);

        foreach (var property in schema.Properties) {
            if (property.Required && (spec.Properties is null || !ContainsProperty(spec.Properties, property.Name))) {
                errors.Add(new ToolError(
                    ToolErrorCodes.SpecMissingRequiredProperty,
                    $"Activity \"{schema.Name}\" at {path} is missing required property \"{property.Name}\".",
                    $"Add \"{property.Name}\" with the correct form: {SpecExpressionValidator.CorrectForm(property, context)}."));
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

                var mismatch = SpecExpressionValidator.FormMismatch(property, value, context);
                if (mismatch is not null) {
                    errors.Add(new ToolError(
                        ToolErrorCodes.SpecValueFormMismatch,
                        $"Property \"{name}\" of \"{schema.Name}\" at {path}: {mismatch}",
                        $"Use the correct form for \"{property.Name}\": {SpecExpressionValidator.CorrectForm(property, context)}."));
                    continue;
                }

                if (SpecExpressionValidator.AllowedValueMismatch(property, value) is { } allowed) {
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

                if (!SpecValidator.IsKnownDirection(argument.Direction)) {
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
                SpecGraphValidator.ValidateFlowchart(spec.Flowchart, path, errors);
            }
        }

        if (schema.Name == "StateMachine") {
            if (spec.StateMachine is not null) {
                SpecGraphValidator.ValidateStateMachine(spec.StateMachine, path, errors);
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

                if (!SpecValidator.IsKnownDirection(argument.Direction)) {
                    errors.Add(new ToolError(
                        ToolErrorCodes.SpecValueFormMismatch,
                        $"{schema.Name} argument \"{argument.Name}\" at {path}.arguments[{i}] has direction \"{argument.Direction}\".",
                        "Use direction \"In\", \"Out\", or \"InOut\"."));
                }

                var formMismatch = SpecExpressionValidator.ArgumentFormMismatch(argument.Value, context);
                if (formMismatch is not null) {
                    errors.Add(new ToolError(
                        ToolErrorCodes.SpecValueFormMismatch,
                        $"{schema.Name} argument \"{argument.Name}\" at {path}.arguments[{i}]: {formMismatch}",
                        SpecExpressionValidator.CorrectArgumentForm(context)));
                }
            }
        }
    }

    private static string BodyHint(string activityName) => activityName switch {
        "InvokeCode" => "Remove the children. InvokeCode takes no activity body; bind its parameters with 'arguments' and put the code in 'Code'.",
        "InvokeWorkflowFile" => "Remove the children, or nest them inside a container activity such as Sequence. InvokeWorkflowFile takes no activity body.",
        _ => "Remove the children, or nest them inside a container activity such as Sequence."
    };

    private static bool TakesSingleBody(ActivitySchema schema) =>
        schema.Body?.Shape == BodyShape.Activity;

    // A Sequence owns a <Sequence.Variables> block wherever it sits, so a nested
    // Sequence may scope variables to itself.
    private static bool IsSequence(ActivitySchema schema) =>
        string.Equals(schema.RenderName, "Sequence", StringComparison.OrdinalIgnoreCase);

    private static bool ContainsProperty(Dictionary<string, string> properties, string name) =>
        properties.ContainsKey(name) || properties.Keys.Any(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase));

    // The closest declared property, so a typo points at the intended name.
    private static string UnknownPropertyHint(ActivitySchema schema, string name, SpecValidationContext context) {
        var known = schema.Properties.Select(p => p.Name).ToList();
        var suggestion = Suggest(name, known);
        if (suggestion is not null) {
            var property = schema.Properties.First(p => p.Name == suggestion);
            return $"Did you mean \"{suggestion}\"? {SpecExpressionValidator.CorrectForm(property, context)}";
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
}
