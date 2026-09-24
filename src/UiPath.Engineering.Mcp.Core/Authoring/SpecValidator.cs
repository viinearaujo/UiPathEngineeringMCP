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
    /// Properties the schema does not know about are tolerated here (a discovered
    /// activity may have a richer surface than the catalog carries); the builder
    /// reports each passthrough as a warning on the render result.
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
                if (!lookup.TryGetValue(name, out var property)) continue; // unknown properties are rendered as a passthrough attribute with a warning
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
