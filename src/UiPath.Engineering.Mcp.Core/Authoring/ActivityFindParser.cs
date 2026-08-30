using System.Text.Json;

namespace UiPath.Engineering.Mcp.Core.Authoring;

/// <summary>
/// Parses <c>uip rpa activities find --output json</c> payloads. The CLI envelope
/// varies by version, so this walks arrays of objects that look like activity hits.
/// </summary>
public static class ActivityFindParser
{
    private static readonly string[] ArrayPropertyNames =
        ["activities", "Activities", "items", "Items", "results", "Results", "data", "Data", "value", "Value"];

    public static IReadOnlyList<DiscoveredActivity> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            return [.. Collect(doc.RootElement).Select(ToDiscovered).Where(a => a is not null)!];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static IEnumerable<JsonElement> Collect(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Object && LooksLikeActivity(item))
                    {
                        yield return item;
                    }
                    else
                    {
                        foreach (var nested in Collect(item)) yield return nested;
                    }
                }
                yield break;
            case JsonValueKind.Object:
                foreach (var name in ArrayPropertyNames)
                {
                    if (element.TryGetProperty(name, out var array) && array.ValueKind is JsonValueKind.Array or JsonValueKind.Object)
                    {
                        foreach (var nested in Collect(array)) yield return nested;
                        yield break;
                    }
                }

                if (element.TryGetProperty("Result", out var result) || element.TryGetProperty("result", out result))
                {
                    foreach (var nested in Collect(result)) yield return nested;
                    if (result.ValueKind is not JsonValueKind.String)
                    {
                        yield break;
                    }
                }

                if (LooksLikeActivity(element))
                {
                    yield return element;
                }
                yield break;
        }
    }

    private static bool LooksLikeActivity(JsonElement element) =>
        element.ValueKind == JsonValueKind.Object
        && (Has(element, "name") || Has(element, "activityName") || Has(element, "className")
            || Has(element, "fullName") || Has(element, "fullTypeName") || Has(element, "typeName")
            || Has(element, "activityClassName"));

    private static bool Has(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
        && !string.IsNullOrWhiteSpace(value.GetString());

    private static DiscoveredActivity? ToDiscovered(JsonElement element)
    {
        var fullTypeName = FirstString(element, "fullName", "fullTypeName", "typeName", "activityClassName", "className");
        var rawName = FirstString(element, "name", "activityName", "displayName") ?? fullTypeName;
        if (string.IsNullOrWhiteSpace(rawName))
        {
            return null;
        }

        var name = ShortName(rawName);
        var xmlNamespace = FirstString(element, "xmlNamespace", "xmlns", "namespace");
        var prefix = FirstString(element, "prefix");
        var (inferredPrefix, inferredNs) = InferNamespace(xmlNamespace, fullTypeName ?? rawName, name);
        var isContainer = FirstBool(element, "isContainer", "container") ?? true;
        var properties = ReadProperties(element);

        return new DiscoveredActivity(
            Name: name,
            FullTypeName: fullTypeName ?? (rawName.Contains('.') ? rawName : null),
            PackageId: FirstString(element, "packageId", "package", "packageName", "nugetPackage"),
            PackageVersion: StripVersion(FirstString(element, "packageVersion", "version")),
            XmlNamespace: xmlNamespace ?? inferredNs,
            Prefix: prefix ?? inferredPrefix,
            IsContainer: isContainer,
            Properties: properties);
    }

    internal static string ShortName(string name)
    {
        var last = name;
        var dot = name.LastIndexOf('.');
        if (dot >= 0 && dot < name.Length - 1)
        {
            last = name[(dot + 1)..];
        }

        var tick = last.IndexOf('`');
        return tick >= 0 ? last[..tick] : last;
    }

    internal static string? StripVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return version;
        }

        return version.Trim().TrimStart('[').TrimEnd(']');
    }

    internal static (string Prefix, string Ns) InferNamespace(string? xmlNamespace, string fullTypeName, string name)
    {
        if (!string.IsNullOrWhiteSpace(xmlNamespace))
        {
            if (xmlNamespace.Contains("/uix", StringComparison.OrdinalIgnoreCase))
            {
                return ("uix", xmlNamespace);
            }

            if (xmlNamespace.Contains("uipath", StringComparison.OrdinalIgnoreCase))
            {
                return ("ui", xmlNamespace);
            }

            return ("", xmlNamespace);
        }

        if (ActivityCatalog.WorkflowFoundationNames.Contains(name))
        {
            return ("", "http://schemas.microsoft.com/netfx/2009/xaml/activities");
        }

        if (fullTypeName.Contains("UIAutomation", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith('N') && name.Length > 1 && char.IsUpper(name[1]))
        {
            return ("uix", "http://schemas.uipath.com/workflow/activities/uix");
        }

        return ("ui", "http://schemas.uipath.com/workflow/activities");
    }

    private static IReadOnlyList<PropertySchema>? ReadProperties(JsonElement element)
    {
        if (!element.TryGetProperty("properties", out var props) && !element.TryGetProperty("Properties", out props))
        {
            return null;
        }

        if (props.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var list = new List<PropertySchema>();
        foreach (var prop in props.EnumerateArray())
        {
            var name = FirstString(prop, "name", "Name");
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var required = FirstBool(prop, "required", "Required") ?? false;
            var kindText = FirstString(prop, "kind", "Kind") ?? "Literal";
            var kind = kindText.Equals("Expression", StringComparison.OrdinalIgnoreCase) ? PropertyKind.Expression
                : kindText.Equals("TypeArgument", StringComparison.OrdinalIgnoreCase) ? PropertyKind.TypeArgument
                : PropertyKind.Literal;
            list.Add(new PropertySchema(name, required, kind));
        }

        return list.Count == 0 ? null : list;
    }

    private static string? FirstString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
            {
                var text = value.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text;
                }
            }
        }

        return null;
    }

    private static bool? FirstBool(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var value))
            {
                continue;
            }

            if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                return value.GetBoolean();
            }

            if (value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }
}
