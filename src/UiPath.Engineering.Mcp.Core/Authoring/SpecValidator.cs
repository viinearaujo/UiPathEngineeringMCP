using UiPath.Engineering.Mcp.Core.Models;

namespace UiPath.Engineering.Mcp.Core.Authoring;

public static class SpecValidator
{
    // Returns all violations; empty list == valid. path e.g. "children[0].children[0]"
    public static List<ToolError> Validate(ActivitySpec spec) =>
        Validate(spec, ActivityCatalog.Fallback);

    public static List<ToolError> Validate(ActivitySpec spec, IActivityCatalog catalog)
    {
        var errors = new List<ToolError>();
        if (spec is null || string.IsNullOrWhiteSpace(spec.Name))
        {
            errors.Add(new ToolError(
                ToolErrorCodes.SpecEmptySpec,
                "The activity spec is empty: 'name' is missing or blank.",
                "Provide a spec with a 'name' matching an activity from the catalog, e.g. { \"name\": \"Sequence\", \"children\": [...] }.",
                "validate_activity_spec"));
            return errors;
        }

        Walk(spec, path: spec.Name, isRoot: true, errors, catalog);
        return errors;
    }

    private static void Walk(ActivitySpec spec, string path, bool isRoot, List<ToolError> errors, IActivityCatalog catalog)
    {
        if (!catalog.TryGet(spec.Name, out var schema))
        {
            var suggestion = catalog.Suggest(spec.Name);
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
        }
        else
        {
            ValidateAgainstSchema(spec, schema, path, isRoot, errors);
        }

        if (spec.Children is not null)
        {
            for (var i = 0; i < spec.Children.Count; i++)
            {
                Walk(spec.Children[i], $"{path}.children[{i}]", isRoot: false, errors, catalog);
            }
        }

        if (spec.Else is not null)
        {
            for (var i = 0; i < spec.Else.Count; i++)
            {
                Walk(spec.Else[i], $"{path}.else[{i}]", isRoot: false, errors, catalog);
            }
        }

        if (spec.Default is not null)
        {
            for (var i = 0; i < spec.Default.Count; i++)
            {
                Walk(spec.Default[i], $"{path}.default[{i}]", isRoot: false, errors, catalog);
            }
        }

        if (spec.Cases is not null)
        {
            for (var i = 0; i < spec.Cases.Count; i++)
            {
                var catchChildren = spec.Cases[i].Children;
                if (catchChildren is null) continue;
                for (var j = 0; j < catchChildren.Count; j++)
                {
                    Walk(catchChildren[j], $"{path}.cases[{i}].children[{j}]", isRoot: false, errors, catalog);
                }
            }
        }

        if (spec.Catches is not null)
        {
            for (var i = 0; i < spec.Catches.Count; i++)
            {
                var catchChildren = spec.Catches[i].Children;
                if (catchChildren is null) continue;
                for (var j = 0; j < catchChildren.Count; j++)
                {
                    Walk(catchChildren[j], $"{path}.catches[{i}].children[{j}]", isRoot: false, errors, catalog);
                }
            }
        }
    }

    private static void ValidateAgainstSchema(ActivitySpec spec, ActivitySchema schema, string path, bool isRoot, List<ToolError> errors)
    {
        var lookup = PropertyLookup(schema);

        foreach (var property in schema.Properties)
        {
            if (property.Required && (spec.Properties is null || !ContainsProperty(spec.Properties, property.Name)))
            {
                errors.Add(new ToolError(
                    ToolErrorCodes.SpecMissingRequiredProperty,
                    $"Activity \"{schema.Name}\" at {path} is missing required property \"{property.Name}\".",
                    $"Add \"{property.Name}\" with the correct form: {CorrectForm(property)}."));
            }
        }

        if (spec.Properties is not null)
        {
            foreach (var (name, value) in spec.Properties)
            {
                if (!lookup.TryGetValue(name, out var property)) continue; // unknown properties are tolerated
                var mismatch = FormMismatch(property, value);
                if (mismatch is not null)
                {
                    errors.Add(new ToolError(
                        ToolErrorCodes.SpecValueFormMismatch,
                        $"Property \"{name}\" of \"{schema.Name}\" at {path}: {mismatch}",
                        $"Use the correct form for \"{property.Name}\": {CorrectForm(property)}."));
                }
            }
        }

        if (spec.Children is { Count: > 0 } && !schema.IsContainer)
        {
            errors.Add(new ToolError(
                ToolErrorCodes.SpecInvalidNesting,
                $"Activity \"{schema.Name}\" at {path} is not a container but has {spec.Children.Count} child(ren).",
                $"Remove the children, or nest them inside a container activity such as Sequence."));
        }

        if (spec.Variables is { Count: > 0 } && !isRoot)
        {
            errors.Add(new ToolError(
                ToolErrorCodes.SpecInvalidNesting,
                $"Activity \"{schema.Name}\" at {path} declares variables, which are only allowed on the root spec.",
                "Move the 'variables' list to the root spec."));
        }

        if (spec.Catches is { Count: > 0 } && schema.Name != "TryCatch")
        {
            errors.Add(new ToolError(
                ToolErrorCodes.SpecInvalidNesting,
                $"Activity \"{schema.Name}\" at {path} declares catches, which are only allowed on TryCatch.",
                "Move the 'catches' list onto a TryCatch activity."));
        }

        if (spec.Else is { Count: > 0 } && schema.Name != "If")
        {
            errors.Add(new ToolError(
                ToolErrorCodes.SpecInvalidNesting,
                $"Activity \"{schema.Name}\" at {path} declares else, which is only allowed on If.",
                "Move the 'else' list onto an If activity (Children is the Then branch)."));
        }

        if (spec.Cases is { Count: > 0 } && schema.Name != "Switch")
        {
            errors.Add(new ToolError(
                ToolErrorCodes.SpecInvalidNesting,
                $"Activity \"{schema.Name}\" at {path} declares cases, which are only allowed on Switch.",
                "Move the 'cases' list onto a Switch activity."));
        }

        if (spec.Default is { Count: > 0 } && schema.Name != "Switch")
        {
            errors.Add(new ToolError(
                ToolErrorCodes.SpecInvalidNesting,
                $"Activity \"{schema.Name}\" at {path} declares default, which is only allowed on Switch.",
                "Move the 'default' list onto a Switch activity."));
        }

        if (spec.Arguments is { Count: > 0 } && schema.Name != "InvokeWorkflowFile")
        {
            errors.Add(new ToolError(
                ToolErrorCodes.SpecInvalidNesting,
                $"Activity \"{schema.Name}\" at {path} declares arguments, which are only allowed on InvokeWorkflowFile.",
                "Move the 'arguments' list onto an InvokeWorkflowFile activity."));
        }

        if (schema.Name == "Switch" && spec.Children is { Count: > 0 })
        {
            errors.Add(new ToolError(
                ToolErrorCodes.SpecInvalidNesting,
                $"Switch at {path} uses 'children'; Switch branches belong in 'cases' and 'default'.",
                "Replace 'children' with 'cases': [{ \"key\": \"1\", \"children\": [...] }] and optional 'default'."));
        }

        if (schema.Name == "Switch" && spec.Cases is not null)
        {
            for (var i = 0; i < spec.Cases.Count; i++)
            {
                if (string.IsNullOrWhiteSpace(spec.Cases[i].Key))
                {
                    errors.Add(new ToolError(
                        ToolErrorCodes.SpecMissingRequiredProperty,
                        $"Switch case at {path}.cases[{i}] is missing required property \"key\".",
                        "Set \"key\" to the literal compared against Expression, e.g. \"1\" or \"Open\"."));
                }
            }
        }

        if (schema.Name == "InvokeWorkflowFile" && spec.Arguments is not null)
        {
            for (var i = 0; i < spec.Arguments.Count; i++)
            {
                var argument = spec.Arguments[i];
                if (string.IsNullOrWhiteSpace(argument.Name))
                {
                    errors.Add(new ToolError(
                        ToolErrorCodes.SpecMissingRequiredProperty,
                        $"Invoke argument at {path}.arguments[{i}] is missing required property \"name\".",
                        "Set \"name\" to the target workflow argument, e.g. \"in_FilePath\"."));
                }

                if (!IsKnownDirection(argument.Direction))
                {
                    errors.Add(new ToolError(
                        ToolErrorCodes.SpecValueFormMismatch,
                        $"Invoke argument \"{argument.Name}\" at {path}.arguments[{i}] has direction \"{argument.Direction}\".",
                        "Use direction \"In\", \"Out\", or \"InOut\"."));
                }
            }
        }
    }

    internal static bool IsKnownDirection(string? direction)
    {
        if (string.IsNullOrWhiteSpace(direction))
        {
            return true; // defaults to In at render time
        }

        return direction.Equals("In", StringComparison.OrdinalIgnoreCase)
            || direction.Equals("Out", StringComparison.OrdinalIgnoreCase)
            || direction.Equals("InOut", StringComparison.OrdinalIgnoreCase)
            || direction.Equals("In/Out", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsProperty(Dictionary<string, string> properties, string name) =>
        properties.ContainsKey(name) || properties.Keys.Any(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase));

    private static string? FormMismatch(PropertySchema property, string value)
    {
        var wrapped = IsExpressionForm(value);
        return property.Kind switch
        {
            PropertyKind.Expression when !wrapped && !IsQuotedStringLiteral(value) =>
                $"value \"{value}\" is not expression-wrapped.",
            PropertyKind.Literal when wrapped =>
                $"value \"{value}\" is expression-wrapped but the property is a literal.",
            PropertyKind.TypeArgument when value.Contains('[') || value.Contains(']') =>
                $"value \"{value}\" contains brackets but the property is a type name.",
            _ => null,
        };
    }

    private static bool IsExpressionForm(string value) =>
        value.Length >= 2 && value.StartsWith('[') && value.EndsWith(']');

    // A "..." value is already a valid VB string-literal expression.
    private static bool IsQuotedStringLiteral(string value) =>
        value.Length >= 2 && value.StartsWith('"') && value.EndsWith('"');

    private static string CorrectForm(PropertySchema property) => property.Kind switch
    {
        PropertyKind.Expression => $"wrap a VB expression in brackets, e.g. \"{property.Name}\": \"[myVar + 1]\" (a quoted VB string literal like \"\\\"text\\\"\" is also accepted).",
        PropertyKind.Literal => $"pass the raw value with no brackets, e.g. \"{property.Name}\": \"Info\".",
        PropertyKind.TypeArgument => $"pass a type name with no brackets, e.g. \"{property.Name}\": \"DataRow\".",
        _ => property.Name,
    };

    // Case-insensitive property lookup per schema, built once per schema instance.
    private static readonly Dictionary<ActivitySchema, IReadOnlyDictionary<string, PropertySchema>> Lookups = new();

    private static IReadOnlyDictionary<string, PropertySchema> PropertyLookup(ActivitySchema schema)
    {
        lock (Lookups)
        {
            if (!Lookups.TryGetValue(schema, out var lookup))
            {
                lookup = schema.Properties.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
                Lookups[schema] = lookup;
            }

            return lookup;
        }
    }
}
