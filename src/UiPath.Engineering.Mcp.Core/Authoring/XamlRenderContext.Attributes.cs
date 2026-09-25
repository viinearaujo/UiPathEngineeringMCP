using System.Xml.Linq;
using UiPath.Engineering.Mcp.Core.Models;

namespace UiPath.Engineering.Mcp.Core.Authoring;

public sealed partial class XamlRenderContext {
    internal List<XAttribute> Attributes(ActivitySpec spec, ActivitySchema schema, string? exclude = null) {
        var attributes = new List<XAttribute>();
        var matched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var property in schema.Properties.OrderBy(p => p.Kind == PropertyKind.TypeArgument ? 0 : 1)) {
            if (IsExcluded(property.Name)) {
                continue;
            }

            var value = PropertyValue(spec, property.Name);
            if (value is null) {
                continue;
            }

            matched.Add(property.Name);
            if (property.Kind == PropertyKind.TypeArgument) {
                var token = TypeToken.Render(value);
                UseAliasesIn(token);
                attributes.Add(new XAttribute(XamlNamespaces.X + "TypeArguments", token));
            } else if (RendersAsArgumentElement(spec, schema, property, value)) {
                continue; // rendered as a property element below
            } else {
                attributes.Add(new XAttribute(property.Name, value));
            }
        }

        foreach (var (key, value) in spec.Properties ?? []) {
            if (matched.Contains(key) || IsExcluded(key)) {
                continue;
            }

            if (UnknownAttribute(schema, key, value) is { } attribute) {
                attributes.Add(attribute);
            }
        }

        return attributes;

        bool IsExcluded(string name) =>
            exclude is not null && string.Equals(name, exclude, StringComparison.OrdinalIgnoreCase);
    }

    // True when a property must leave the attribute list because it is
    // rendered as a typed argument property element: always for a typed
    // Assign<T>, and for any non-literal expression in a C# project.
    internal bool RendersAsArgumentElement(ActivitySpec spec, ActivitySchema schema, PropertySchema property, string value) {
        if (property.Kind != PropertyKind.Expression) {
            return false;
        }

        if (schema.Name == "Assign" && property.Name is "To" or "Value") {
            return TypedAssign(spec) || _settings.IsCSharp;
        }

        // In a C# project only converter literals (number, boolean, TimeSpan,
        // {x:Null}) stay in attribute form; a quoted string or an identifier is
        // an expression and must render through CSharpValue/CSharpReference.
        return _settings.IsCSharp && !ExpressionValue.IsCSharpAttributeLiteral(value);
    }

    internal void AddExpressionProperties(XElement element, ActivitySpec spec, ActivitySchema schema) {
        foreach (var property in schema.Properties) {
            if (property.Kind != PropertyKind.Expression) {
                continue;
            }

            var value = PropertyValue(spec, property.Name);
            if (value is null || !RendersAsArgumentElement(spec, schema, property, value)) {
                continue;
            }

            AddArgumentProperty(element, schema, property.Name, value,
                ArgumentToken(spec, schema, property.Name), ArgumentDirectionOf(schema, property.Name));
        }
    }

    internal void AddArgumentProperty(
        XElement element, ActivitySchema schema, string propertyName, string? value, string token, ArgumentDirection direction) {
        if (value is null) {
            return;
        }

        UseAliasesIn(token);
        var argumentName = ArgumentElement(direction);
        var argument = new XElement(XamlNamespaces.Wf + argumentName, new XAttribute(XamlNamespaces.X + "TypeArguments", token));
        AddBinding(argument, value, token, direction);
        element.Add(new XElement(Ns(schema) + schema.RenderName + "." + propertyName, argument));
    }

    // Fills an argument element: a C# project gets the typed
    // CSharpValue/CSharpReference child, a VB project keeps the [bracket]
    // text the attribute form used to carry.
    internal void AddBinding(XElement argument, string? value, string token, ArgumentDirection direction) {
        if (string.IsNullOrEmpty(value)) {
            return;
        }

        if (!_settings.IsCSharp) {
            argument.Add(value);
            return;
        }

        var isWrite = direction is ArgumentDirection.Out or ArgumentDirection.InOut;
        argument.Add(new XElement(XamlNamespaces.Wf + (isWrite ? "CSharpReference" : "CSharpValue"),
            new XAttribute(XamlNamespaces.X + "TypeArguments", token),
            Unwrap(value)));
    }

    internal ArgumentDirection ArgumentDirectionOf(ActivitySchema schema, string propertyName) =>
        ActivityCatalog.ExpressionArgument(schema.Name, propertyName)?.Direction ?? ArgumentDirection.In;

    // The x:TypeArguments token an expression property binds through. Types
    // that vary with the spec's own TypeArgument (Assign, Switch, ForEach)
    // are derived here; fixed types come from the catalog; anything else is
    // assumed x:Object and reported.
    internal string ArgumentToken(ActivitySpec spec, ActivitySchema schema, string propertyName) {
        var known = ActivityCatalog.ExpressionArgument(schema.Name, propertyName);
        if (known is { Token.Length: > 0 } fixedType) {
            return fixedType.Token;
        }

        if (known is not null) {
            var declared = DeclaredTypeArgument(spec) ?? "x:Object";
            // ui:ForEach.Values is InArgument<IEnumerable> (non-generic); the
            // element type T comes from the activity's own x:TypeArguments.
            return schema.Name == "ForEach" ? "sc:IEnumerable" : declared;
        }

        Warn($"The argument type of \"{schema.Name}.{propertyName}\" is not in the built-in catalog, so its C# binding was rendered as x:Object. If the property is not InArgument<Object>, correct the type in the emitted XAML — run validate_project to confirm.");
        return "x:Object";
    }

    // The spec's own TypeArgument property rendered as an x:TypeArguments
    // token, or null when the spec does not declare one.
    internal static string? DeclaredTypeArgument(ActivitySpec spec) {
        var declared = PropertyValue(spec, "TypeArgument");
        return string.IsNullOrWhiteSpace(declared) ? null : TypeToken.Render(declared);
    }

    internal static bool TypedAssign(ActivitySpec spec) =>
        !string.IsNullOrWhiteSpace(PropertyValue(spec, "TypeArgument"));

    // A property the schema does not know about is passed through, but never
    // silently. Two corrections apply so the passthrough cannot produce XAML
    // that looks valid but is not: an unprefixed XAML-language member
    // (typeArguments, key) is qualified as x: with its canonical casing, and a
    // property naming an xmlns prefix this builder does not declare is omitted
    // with a warning rather than emitted as a name LINQ-to-XML cannot represent.
    internal XAttribute? UnknownAttribute(ActivitySchema schema, string key, string value) {
        var colon = key.IndexOf(':');
        if (colon > 0 && colon < key.Length - 1) {
            var prefix = key[..colon];
            if (KnownPrefixNamespace(prefix) is { } ns) {
                Warn($"Property \"{key}\" is not in the schema for \"{schema.Name}\"; it was passed through as a {prefix}: attribute. Verify it against the activity's documentation.");
                return new XAttribute(ns + key[(colon + 1)..], value);
            }

            Warn($"Property \"{key}\" names the xmlns prefix \"{prefix}\", which this builder does not declare, so it was omitted — emitting it would produce a workflow that cannot load. Declare the prefix in the file (activities get-default-xaml), or drop the property.");
            return null;
        }

        if (key.Equals("TypeArguments", StringComparison.OrdinalIgnoreCase)) {
            Warn($"Property \"{key}\" of \"{schema.Name}\" is the XAML language member x:TypeArguments, not an activity property; it was rendered as \"x:TypeArguments\". An unprefixed {key} attribute is not valid XAML.");
            return new XAttribute(XamlNamespaces.X + "TypeArguments", value);
        }

        if (key.Equals("Key", StringComparison.OrdinalIgnoreCase)) {
            Warn($"Property \"{key}\" of \"{schema.Name}\" is the XAML language member x:Key, not an activity property; it was rendered as \"x:Key\". An unprefixed {key} attribute is not valid XAML.");
            return new XAttribute(XamlNamespaces.X + "Key", value);
        }

        Warn($"Property \"{key}\" is not in the schema for \"{schema.Name}\"; it was passed through as an attribute. Verify the name against the activity's documentation (activities get-default-xaml).");
        return new XAttribute(key, value);
    }

    internal static XNamespace? KnownPrefixNamespace(string prefix) => prefix switch {
        "x" => XamlNamespaces.X,
        "sap" => XamlNamespaces.Sap,
        "sap2010" => XamlNamespaces.Sap2010,
        "mc" => XamlNamespaces.Mc,
        "ui" => XamlNamespaces.Ui,
        _ => null
    };
}
