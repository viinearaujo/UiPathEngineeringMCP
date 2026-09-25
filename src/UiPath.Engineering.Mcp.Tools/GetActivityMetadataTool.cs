using System.ComponentModel;
using System.Diagnostics;
using ModelContextProtocol.Server;
using UiPath.Engineering.Mcp.Core;
using UiPath.Engineering.Mcp.Core.Abstractions;
using UiPath.Engineering.Mcp.Core.Authoring;
using UiPath.Engineering.Mcp.Core.Models;
using UiPath.Engineering.Mcp.Core.Parsing;

namespace UiPath.Engineering.Mcp.Tools;

/// <summary>
/// Reports one activity's complete authoring surface: every property with its CLR type,
/// argument direction, required flag, default, allowed values, and content-property marker,
/// plus how the activity receives its body. The surface comes from the resolved project
/// catalog, which reflects over the installed package assemblies
/// (<see cref="ActivitySchemaReflector"/>) and falls back to the curated card and the
/// package's default XAML — this tool only reports it.
/// </summary>
[McpServerToolType]
public sealed class GetActivityMetadataTool {
    private readonly IActivityCatalogResolver _catalogResolver;
    private readonly IFilesystemProvider _filesystem;

    public GetActivityMetadataTool(IActivityCatalogResolver catalogResolver, IFilesystemProvider filesystem) {
        _catalogResolver = catalogResolver;
        _filesystem = filesystem;
    }

    [McpServerTool(
        UseStructuredContent = true,
        Title = "Get Activity Metadata",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true),
     Description("Full authoring surface of one activity (properties, types, required, defaults, body shape). Prefer over guessing. Next: validate_activity_spec.")]
    public async Task<ToolResult> GetActivityMetadata(
        [Description("Activity name to describe, e.g. 'LogMessage', 'If', 'NClick'. Both the toolbox label ('While') and the emitted type ('InterruptibleWhile') resolve to the same schema.")] string name,
        [Description("Optional absolute path to the UiPath project directory. When set, the surface comes from that project's package catalog (which reflects over the installed activity assemblies); otherwise the built-in fallback catalog is used.")] string? projectPath = null,
        CancellationToken cancellationToken = default) {

        var sw = Stopwatch.StartNew();

        if (string.IsNullOrWhiteSpace(name)) {
            return ToolResults.Failure(new ToolError(
                ToolErrorCodes.InvalidArgument,
                "name is required.",
                "Pass the activity name, e.g. 'LogMessage' or 'If'."), sw);
        }

        if (!string.IsNullOrWhiteSpace(projectPath)
            && ToolResults.GuardProject(_filesystem, projectPath, sw) is { } guardFailure) {
            return guardFailure;
        }

        var catalog = await _catalogResolver.ResolveAsync(projectPath, cancellationToken);
        var query = name.Trim();
        if (!catalog.TryGet(query, out var schema)) {
            var suggestion = catalog.Suggest(query);
            var fixHint = suggestion is null
                ? "Call find_activity for an in-project activity, or recommend_activities for a natural-language step."
                : $"Did you mean '{suggestion}'? Re-run with that name.";
            return ToolResults.Failure(new ToolError(
                ToolErrorCodes.ActivityNotFound,
                $"Activity '{query}' is not in the catalog (source: {catalog.Source}).",
                fixHint,
                "recommend_activities"), sw);
        }

        var warnings = new List<string>();
        if (!schema.PropertiesAreComplete) {
            warnings.Add("This surface is not authoritative for required-ness: it came from the curated card or the package's default-XAML sample, which carries only properties whose value differs from the type default. "
                + "A required property left at its default is invisible here. Treat a property the surface does not list as unverified rather than absent.");
        }

        if (ActivityCatalogResolver.DiscoveryWarning(catalog) is { } discoveryWarning) {
            warnings.Add(discoveryWarning);
        }

        var properties = schema.Properties.Select(ToProperty).ToList();
        return ToolResults.Ok(
            $"'{schema.Name}' has {properties.Count} property(s){(schema.IsContainer ? " and is a container" : string.Empty)} (source: {catalog.Source}).",
            new {
                name = schema.Name,
                renderName = schema.RenderName,
                elementName = schema.ElementName,
                fullTypeName = schema.FullTypeName,
                prefix = schema.Prefix,
                xmlNamespace = schema.XmlNamespace,
                packageId = schema.PackageId,
                packageVersion = schema.PackageVersion,
                isContainer = schema.IsContainer,
                experimental = schema.Experimental,
                propertiesAreComplete = schema.PropertiesAreComplete,
                body = schema.Body is null ? null : new {
                    shape = ToCamel(schema.Body.Shape.ToString()),
                    property = schema.Body.Property,
                    delegateType = schema.Body.DelegateType,
                    iteratorName = schema.Body.IteratorName
                },
                contentProperty = schema.Properties.FirstOrDefault(p => p.IsContentProperty)?.Name,
                requiredProperties = schema.Properties.Where(p => p.Required).Select(p => p.Name).ToList(),
                properties
            }, sw, warnings);
    }

    private static object ToProperty(PropertySchema property) => new {
        name = property.Name,
        kind = ToCamel(property.Kind.ToString()),
        required = property.Required,
        clrType = property.ClrType,
        direction = property.Direction is { } direction ? ToCamel(direction.ToString()) : null,
        defaultValue = property.Default,
        allowedValues = property.AllowedValues,
        isContentProperty = property.IsContentProperty
    };

    // PropertyKind.Expression -> "expression", ArgumentDirection.InOut -> "inOut",
    // BodyShape.ActivityCollection -> "activityCollection". The response contract is camelCase.
    internal static string ToCamel(string value) =>
        value.Length == 0 || char.IsLower(value[0])
            ? value
            : char.ToLowerInvariant(value[0]) + value[1..];
}