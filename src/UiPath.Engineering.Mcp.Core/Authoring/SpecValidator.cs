using UiPath.Engineering.Mcp.Core.Models;

namespace UiPath.Engineering.Mcp.Core.Authoring;

/// <summary>
/// Catalog and project settings carried through a validate walk.
/// </summary>
public sealed record SpecValidationContext(IActivityCatalog Catalog, ProjectXamlSettings Settings);

public static class SpecValidator {
    private const int MaxLookups = 256;

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

        var context = new SpecValidationContext(catalog, settings ?? ProjectXamlSettings.Default);
        Walk(spec, path: spec.Name, isRoot: true, errors, context);
        return errors;
    }

    private static void Walk(ActivitySpec spec, string path, bool isRoot, List<ToolError> errors, SpecValidationContext context) {
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
            SpecSchemaValidator.ValidateAgainstSchema(spec, schema, path, isRoot, errors, context);
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

    internal static bool IsKnownDirection(string? direction) {
        if (string.IsNullOrWhiteSpace(direction)) {
            return true; // defaults to In at render time
        }

        return direction.Equals("In", StringComparison.OrdinalIgnoreCase)
            || direction.Equals("Out", StringComparison.OrdinalIgnoreCase)
            || direction.Equals("InOut", StringComparison.OrdinalIgnoreCase)
            || direction.Equals("In/Out", StringComparison.OrdinalIgnoreCase);
    }

    // Case-insensitive property lookup per schema, built once per schema instance.
    // Capped so a long-lived server that resolves many catalog versions cannot grow forever.
    private static readonly Dictionary<ActivitySchema, IReadOnlyDictionary<string, PropertySchema>> Lookups = new();

    internal static IReadOnlyDictionary<string, PropertySchema> PropertyLookup(ActivitySchema schema) {
        lock (Lookups) {
            if (!Lookups.TryGetValue(schema, out var lookup)) {
                if (Lookups.Count >= MaxLookups) {
                    Lookups.Clear();
                }

                lookup = schema.Properties.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
                Lookups[schema] = lookup;
            }

            return lookup;
        }
    }
}
