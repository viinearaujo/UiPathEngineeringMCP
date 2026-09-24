using System.Text.Json;
using System.Xml.Linq;

namespace UiPath.Engineering.Mcp.Core.Authoring;

/// <summary>
/// The property surface read off an <c>activities get-default-xaml</c> starter.
/// The starter carries only the properties whose value differs from the type
/// default, so this is a sample of the real surface rather than a complete one:
/// it names properties the activity actually declares instead of the
/// DisplayName-only schema a bare discovery hit produced.
/// </summary>
public sealed record DefaultXamlSurface(
    string ElementName,
    string XmlNamespace,
    bool IsContainer,
    IReadOnlyList<PropertySchema> Properties);

/// <summary>
/// Parses the starter element that <c>uip rpa activities get-default-xaml</c>
/// returns. The CLI serializes a default-constructed instance, so the element
/// carries the activity's real property names and its namespace — information
/// the hand-written fallback catalog cannot know for a package activity.
/// </summary>
public static class DefaultXamlParser {
    // Attributes that are XAML infrastructure or element identity, never an
    // activity's settable property.
    private static readonly HashSet<string> IgnoredAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Class", "Name", "Key"
    };

    // Properties of the CLI response envelope that hold the starter payload.
    private static readonly string[] PayloadPropertyNames =
        ["defaultXaml", "xaml", "defaultXamlString", "activityXaml", "value", "result"];

    /// <summary>
    /// Pulls the starter XAML out of a CLI response: an envelope whose Data (or
    /// the payload itself) carries the element as a string, a bare JSON string,
    /// or raw XML.
    /// </summary>
    public static string? ExtractXaml(string? stdout) {
        if (string.IsNullOrWhiteSpace(stdout)) {
            return null;
        }

        var text = stdout.Trim();
        if (text.StartsWith('<')) {
            return text;
        }

        try {
            using var document = JsonDocument.Parse(text);
            return ExtractFromElement(document.RootElement);
        } catch (JsonException) {
            // The CLI prints banner text ahead of the payload; retry on the
            // outermost brace pair.
            var start = text.IndexOf('{');
            var end = text.LastIndexOf('}');
            if (start < 0 || end <= start) {
                return null;
            }

            try {
                using var document = JsonDocument.Parse(text[start..(end + 1)]);
                return ExtractFromElement(document.RootElement);
            } catch (JsonException) {
                return null;
            }
        }
    }

    private static string? ExtractFromElement(JsonElement element) {
        if (element.ValueKind == JsonValueKind.String) {
            var value = element.GetString();
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        if (element.ValueKind != JsonValueKind.Object) {
            return null;
        }

        if (Lookup(element, "data") is { } data && ExtractFromElement(data) is { } nested) {
            return nested;
        }

        foreach (var name in PayloadPropertyNames) {
            if (Lookup(element, name) is { ValueKind: JsonValueKind.String } property) {
                var value = property.GetString();
                if (!string.IsNullOrWhiteSpace(value) && value.TrimStart().StartsWith('<')) {
                    return value.Trim();
                }
            }
        }

        return null;
    }

    private static JsonElement? Lookup(JsonElement element, string name) {
        foreach (var property in element.EnumerateObject()) {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) {
                return property.Value;
            }
        }

        return null;
    }

    public static DefaultXamlSurface? Parse(string? xaml) {
        if (string.IsNullOrWhiteSpace(xaml)) {
            return null;
        }

        XElement? activity;
        try {
            activity = FindActivity(XDocument.Parse(xaml).Root);
        } catch (Exception ex) when (ex is System.Xml.XmlException or InvalidOperationException) {
            return null;
        }

        if (activity is null) {
            return null;
        }

        var properties = new List<PropertySchema>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var isContainer = false;

        foreach (var attribute in activity.Attributes()) {
            if (attribute.IsNamespaceDeclaration) {
                continue;
            }

            var local = attribute.Name.LocalName;
            if (local.Contains('.') || IgnoredAttributes.Contains(local)) {
                continue;
            }

            Add(local.Equals("TypeArguments", StringComparison.OrdinalIgnoreCase)
                ? new PropertySchema("TypeArgument", false, PropertyKind.TypeArgument)
                : new PropertySchema(local, false, PropertyKind.Literal));
        }

        foreach (var child in activity.Elements()) {
            var local = child.Name.LocalName;
            var dot = local.LastIndexOf('.');
            if (dot > 0 && dot < local.Length - 1) {
                var owner = local[..dot];
                var property = local[(dot + 1)..];
                if (!owner.Equals(activity.Name.LocalName, StringComparison.OrdinalIgnoreCase)) {
                    continue;
                }

                // A property element holding an ActivityAction / Sequence is the
                // container body slot, not a settable argument.
                if (IsBodySlot(child)) {
                    isContainer = true;
                    continue;
                }

                Add(new PropertySchema(property, false, PropertyKind.Expression));
                continue;
            }

            // A direct child that is not an argument wrapper means the element's
            // content property is its body (ForEach/While/Switch-case-style).
            if (!IsInfrastructure(local)) {
                isContainer = true;
            }
        }

        return new DefaultXamlSurface(activity.Name.LocalName, activity.Name.NamespaceName, isContainer, properties);

        void Add(PropertySchema property) {
            if (seen.Add(property.Name)) {
                properties.Add(property);
            }
        }
    }

    private static bool IsBodySlot(XElement child) =>
        child.HasElements
        && child.Elements().Any(e => e.Name.LocalName is "ActivityAction" or "Sequence");

    private static bool IsInfrastructure(string local) =>
        local.Contains('.')
        || local is "DelegateInArgument" or "DelegateOutArgument"
            or "InArgument" or "OutArgument" or "InOutArgument" or "Collection" or "Dictionary";

    // A starter may be returned wrapped in a synthetic element; the wrapper's
    // first real activity is the surface. A bare element is the activity itself.
    private static XElement? FindActivity(XElement? element) {
        if (element is null) {
            return null;
        }

        if (!element.Name.LocalName.Equals("Wrapper", StringComparison.OrdinalIgnoreCase)) {
            return element;
        }

        return element.Elements().FirstOrDefault();
    }
}