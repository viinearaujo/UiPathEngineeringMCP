using System.Xml.Linq;
using UiPath.Engineering.Mcp.Core.Models;
using UiPath.Engineering.Mcp.Core.Parsing;

namespace UiPath.Engineering.Mcp.Core.Authoring;

public sealed record XamlBuildResult(bool Success, string? Xaml, List<ToolError> Errors);

// Renders a validated ActivitySpec into Studio-valid XAML. Expressions and
// literals pass through verbatim (form is the validator's job). Special shapes
// (If / TryCatch / ForEach) are explicit code below; everything else renders as
// a generic element with attribute-mapped properties and nested children.
//
// Note: If has no Else branch in the spec model — Children = Then branch only.
public static class XamlBuilder
{
    private static readonly XNamespace Wf = "http://schemas.microsoft.com/netfx/2009/xaml/activities";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly XNamespace Mc = "http://schemas.openxmlformats.org/markup-compatibility/2006";
    private static readonly XNamespace Sap = "http://schemas.microsoft.com/netfx/2009/xaml/activities/presentation";
    private static readonly XNamespace Sap2010 = "http://schemas.microsoft.com/netfx/2010/xaml/activities/presentation";
    private static readonly XNamespace Ui = "http://schemas.uipath.com/workflow/activities";

    // Renders a validated spec as a fragment (elements only, no <Activity> root).
    public static XamlBuildResult RenderFragment(ActivitySpec spec)
    {
        var errors = SpecValidator.Validate(spec);
        if (errors.Count > 0) return new XamlBuildResult(false, null, errors);

        try
        {
            var element = RenderElement(spec, includeVariables: true);
            AddFragmentNamespaceDeclarations(element, spec);
            return new XamlBuildResult(true, element.ToString(), []);
        }
        catch (Exception ex)
        {
            return new XamlBuildResult(false, null, [RenderFailed(ex)]);
        }
    }

    // Renders a full workflow file: <Activity x:Class="..." …>{fragment}</Activity>.
    // Validates first (never renders an invalid spec), then round-trips the output
    // through XamlWorkflowParser to prove the file parses.
    public static XamlBuildResult RenderWorkflowFile(ActivitySpec spec, string xamlClassName)
    {
        var errors = SpecValidator.Validate(spec);
        if (errors.Count > 0) return new XamlBuildResult(false, null, errors);

        try
        {
            var root = new XElement(Wf + "Activity",
                new XAttribute(Mc + "Ignorable", "sap sap2010"),
                new XAttribute(X + "Class", xamlClassName),
                new XAttribute("xmlns", Wf.NamespaceName),
                new XAttribute(XNamespace.Xmlns + "mc", Mc.NamespaceName),
                new XAttribute(XNamespace.Xmlns + "sap", Sap.NamespaceName),
                new XAttribute(XNamespace.Xmlns + "sap2010", Sap2010.NamespaceName),
                new XAttribute(XNamespace.Xmlns + "x", X.NamespaceName));

            // Root variables live on a Sequence; wrap a non-Sequence root so they
            // still have a valid home.
            var effective = spec;
            if (spec.Variables is { Count: > 0 } && !string.Equals(spec.Name, "Sequence", StringComparison.OrdinalIgnoreCase))
            {
                effective = new ActivitySpec
                {
                    Name = "Sequence",
                    Variables = spec.Variables,
                    Children = [WithoutVariables(spec)],
                };
            }

            root.Add(RenderElement(effective, includeVariables: true));
            if (UsesNamespace(effective, Ui))
            {
                root.Add(new XAttribute(XNamespace.Xmlns + "ui", Ui.NamespaceName));
            }

            var xaml = root.ToString();

            var parsed = new XamlWorkflowParser().Parse(xamlClassName + ".xaml", xamlClassName + ".xaml", xaml);
            if (parsed.HasParseError)
            {
                return new XamlBuildResult(false, null, [new ToolError(
                    ToolErrorCodes.XamlRoundtripFailed,
                    $"The generated XAML failed to parse when read back: {parsed.ParseError}",
                    "The spec validated but produced malformed XAML; simplify the spec or report this as an authoring bug.")]);
            }

            return new XamlBuildResult(true, xaml, []);
        }
        catch (Exception ex)
        {
            return new XamlBuildResult(false, null, [RenderFailed(ex)]);
        }
    }

    private static ToolError RenderFailed(Exception ex) => new(
        ToolErrorCodes.XamlRenderFailed,
        $"Failed to render the spec as XAML: {ex.Message}",
        "Fix the offending property values and retry; run validate_activity_spec for detailed property guidance.");

    private static ActivitySpec WithoutVariables(ActivitySpec spec) => new()
    {
        Name = spec.Name,
        Properties = spec.Properties,
        Children = spec.Children,
        Catches = spec.Catches,
    };

    private static XElement RenderElement(ActivitySpec spec, bool includeVariables)
    {
        if (!ActivityCatalog.TryGet(spec.Name, out var schema))
        {
            throw new InvalidOperationException($"Unknown activity \"{spec.Name}\".");
        }

        var element = schema.Name switch
        {
            "If" => RenderIf(spec, schema),
            "TryCatch" => RenderTryCatch(spec, schema),
            "ForEach" => RenderForEach(spec, schema),
            _ => RenderGeneric(spec, schema),
        };

        if (includeVariables && spec.Variables is { Count: > 0 })
        {
            element.AddFirst(RenderVariables(spec.Variables));
        }

        return element;
    }

    private static XElement RenderGeneric(ActivitySpec spec, ActivitySchema schema)
    {
        var element = new XElement(Ns(schema) + schema.Name, RenderAttributes(spec, schema));
        AddChildren(element, spec.Children);
        return element;
    }

    private static XElement RenderIf(ActivitySpec spec, ActivitySchema schema)
    {
        var element = new XElement(Wf + "If", RenderAttributes(spec, schema));
        element.Add(new XElement(Wf + "If.Then", RenderedChildren(spec.Children)));
        return element;
    }

    private static XElement RenderTryCatch(ActivitySpec spec, ActivitySchema schema)
    {
        var element = new XElement(Wf + "TryCatch", RenderAttributes(spec, schema));
        element.Add(new XElement(Wf + "TryCatch.Try", RenderedChildren(spec.Children)));

        var catches = new XElement(Wf + "TryCatch.Catches");
        foreach (var catchSpec in spec.Catches ?? [])
        {
            catches.Add(new XElement(Wf + "Catch",
                new XAttribute(X + "TypeArguments", catchSpec.Exception),
                new XElement(Wf + "ActivityAction",
                    new XAttribute(X + "TypeArguments", catchSpec.Exception),
                    new XElement(Wf + "DelegateInArgument",
                        new XAttribute(X + "TypeArguments", catchSpec.Exception),
                        new XAttribute("Name", "ex")),
                    RenderedChildren(catchSpec.Children))));
        }
        element.Add(catches);
        return element;
    }

    private static XElement RenderForEach(ActivitySpec spec, ActivitySchema schema)
    {
        var typeArgument = GetPropertyValue(spec, schema, "TypeArgument") ?? "Object";
        var itemName = GetPropertyValue(spec, schema, "ItemName") ?? "item";

        var element = new XElement(Wf + "ForEach", RenderAttributes(spec, schema, exclude: "ItemName"));
        element.Add(new XElement(Wf + "ActivityAction",
            new XAttribute(X + "TypeArguments", typeArgument),
            new XElement(Wf + "DelegateInArgument",
                new XAttribute(X + "TypeArguments", typeArgument),
                new XAttribute("Name", itemName)),
            RenderedChildren(spec.Children)));
        return element;
    }

    private static XElement RenderVariables(List<VariableSpec> variables) =>
        new(Wf + "Sequence.Variables", variables.Select(v =>
        {
            var element = new XElement(Wf + "Variable",
                new XAttribute(X + "TypeArguments", VariableType(v.Type)),
                new XAttribute("Name", v.Name));
            if (v.Default is not null)
            {
                element.Add(new XAttribute("Default", v.Default));
            }
            return element;
        }));

    // Bare type names ("Int32") map into the x: namespace; anything already
    // qualified ("x:Int32", "System.Data.DataTable") passes through verbatim.
    private static string VariableType(string type) =>
        type.Contains(':') || type.Contains('.') ? type : "x:" + type;

    private static void AddChildren(XElement element, List<ActivitySpec>? children)
    {
        foreach (var child in children ?? [])
        {
            element.Add(RenderElement(child, includeVariables: false));
        }
    }

    private static IEnumerable<XElement> RenderedChildren(List<ActivitySpec>? children) =>
        (children ?? []).Select(child => RenderElement(child, includeVariables: false));

    // Properties → attributes. TypeArgument properties render first as x:TypeArguments
    // (UiPath's conventional order); the rest follow schema order. Spec keys match
    // schema property names case-insensitively; unknown spec keys pass through as-is.
    private static List<XAttribute> RenderAttributes(ActivitySpec spec, ActivitySchema schema, string? exclude = null)
    {
        var attributes = new List<XAttribute>();
        var matched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var property in schema.Properties.OrderBy(p => p.Kind == PropertyKind.TypeArgument ? 0 : 1))
        {
            if (IsExcluded(property.Name)) continue;
            var value = GetPropertyValue(spec, schema, property.Name);
            if (value is null) continue;
            matched.Add(property.Name);
            attributes.Add(property.Kind == PropertyKind.TypeArgument
                ? new XAttribute(X + "TypeArguments", value)
                : new XAttribute(property.Name, value));
        }

        foreach (var (key, value) in spec.Properties ?? [])
        {
            if (matched.Contains(key) || IsExcluded(key)) continue;
            attributes.Add(new XAttribute(key, value));
        }

        return attributes;

        bool IsExcluded(string name) =>
            exclude is not null && string.Equals(name, exclude, StringComparison.OrdinalIgnoreCase);
    }

    private static string? GetPropertyValue(ActivitySpec spec, ActivitySchema schema, string propertyName)
    {
        if (spec.Properties is null) return null;
        if (spec.Properties.TryGetValue(propertyName, out var exact)) return exact;
        foreach (var (key, value) in spec.Properties)
        {
            if (string.Equals(key, propertyName, StringComparison.OrdinalIgnoreCase)) return value;
        }
        return null;
    }

    private static XNamespace Ns(ActivitySchema schema) => schema.XmlNamespace;

    // A fragment has no <Activity> root to carry declarations, so its root element
    // declares the namespaces it needs (appended after the property attributes).
    private static void AddFragmentNamespaceDeclarations(XElement root, ActivitySpec spec)
    {
        root.Add(new XAttribute("xmlns", Wf.NamespaceName));
        root.Add(new XAttribute(XNamespace.Xmlns + "x", X.NamespaceName));
        if (UsesNamespace(spec, Ui))
        {
            root.Add(new XAttribute(XNamespace.Xmlns + "ui", Ui.NamespaceName));
        }
    }

    private static bool UsesNamespace(ActivitySpec spec, XNamespace ns)
    {
        if (ActivityCatalog.TryGet(spec.Name, out var schema) && schema.XmlNamespace == ns.NamespaceName)
        {
            return true;
        }
        foreach (var child in spec.Children ?? [])
        {
            if (UsesNamespace(child, ns)) return true;
        }
        foreach (var catchSpec in spec.Catches ?? [])
        {
            foreach (var child in catchSpec.Children ?? [])
            {
                if (UsesNamespace(child, ns)) return true;
            }
        }
        return false;
    }
}
