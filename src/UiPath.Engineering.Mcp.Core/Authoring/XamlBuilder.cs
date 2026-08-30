using System.Xml.Linq;
using UiPath.Engineering.Mcp.Core.Models;
using UiPath.Engineering.Mcp.Core.Parsing;

namespace UiPath.Engineering.Mcp.Core.Authoring;

public sealed record XamlBuildResult(bool Success, string? Xaml, List<ToolError> Errors);

// Renders a validated ActivitySpec into Studio-valid XAML. Expressions and
// literals pass through verbatim (form is the validator's job). Special shapes
// (If / Switch / TryCatch / ForEach / InvokeWorkflowFile arguments) are explicit
// code below; everything else renders as a generic element with attribute-mapped
// properties and nested children.
//
// If: Children = Then branch; Else = Else branch (omitted when empty).
// Switch: Cases keyed by literal; Default = fallback branch.
// InvokeWorkflowFile: Arguments = In/Out/InOut mappings.
public static class XamlBuilder
{
    private static readonly XNamespace Wf = "http://schemas.microsoft.com/netfx/2009/xaml/activities";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly XNamespace Mc = "http://schemas.openxmlformats.org/markup-compatibility/2006";
    private static readonly XNamespace Sap = "http://schemas.microsoft.com/netfx/2009/xaml/activities/presentation";
    private static readonly XNamespace Sap2010 = "http://schemas.microsoft.com/netfx/2010/xaml/activities/presentation";
    private static readonly XNamespace Ui = "http://schemas.uipath.com/workflow/activities";
    private static readonly XNamespace Scg = "clr-namespace:System.Collections.Generic;assembly=System.Private.CoreLib";

    // Renders a validated spec as a fragment (elements only, no <Activity> root).
    public static XamlBuildResult RenderFragment(ActivitySpec spec) =>
        RenderFragment(spec, ActivityCatalog.Fallback);

    public static XamlBuildResult RenderFragment(ActivitySpec spec, IActivityCatalog catalog)
    {
        var errors = SpecValidator.Validate(spec, catalog);
        if (errors.Count > 0) return new XamlBuildResult(false, null, errors);

        try
        {
            var element = RenderElement(spec, includeVariables: true, catalog);
            AddFragmentNamespaceDeclarations(element, spec, catalog);
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
    public static XamlBuildResult RenderWorkflowFile(ActivitySpec spec, string xamlClassName) =>
        RenderWorkflowFile(spec, xamlClassName, ActivityCatalog.Fallback);

    public static XamlBuildResult RenderWorkflowFile(ActivitySpec spec, string xamlClassName, IActivityCatalog catalog)
    {
        var errors = SpecValidator.Validate(spec, catalog);
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

            root.Add(RenderElement(effective, includeVariables: true, catalog));
            if (UsesNamespace(effective, Ui, catalog))
            {
                root.Add(new XAttribute(XNamespace.Xmlns + "ui", Ui.NamespaceName));
            }

            if (UsesInvokeArguments(effective))
            {
                root.Add(new XAttribute(XNamespace.Xmlns + "scg", Scg.NamespaceName));
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
        Else = spec.Else,
        Cases = spec.Cases,
        Default = spec.Default,
        Arguments = spec.Arguments,
    };

    private static XElement RenderElement(ActivitySpec spec, bool includeVariables, IActivityCatalog catalog)
    {
        if (!catalog.TryGet(spec.Name, out var schema))
        {
            throw new InvalidOperationException($"Unknown activity \"{spec.Name}\".");
        }

        var element = schema.Name switch
        {
            "If" => RenderIf(spec, schema, catalog),
            "Switch" => RenderSwitch(spec, schema, catalog),
            "TryCatch" => RenderTryCatch(spec, schema, catalog),
            "ForEach" => RenderForEach(spec, schema, catalog),
            "InvokeWorkflowFile" => RenderInvoke(spec, schema, catalog),
            _ => RenderGeneric(spec, schema, catalog),
        };

        if (includeVariables && spec.Variables is { Count: > 0 })
        {
            element.AddFirst(RenderVariables(spec.Variables));
        }

        return element;
    }

    private static XElement RenderGeneric(ActivitySpec spec, ActivitySchema schema, IActivityCatalog catalog)
    {
        var element = new XElement(Ns(schema) + schema.Name, RenderAttributes(spec, schema));
        AddChildren(element, spec.Children, catalog);
        return element;
    }

    private static XElement RenderIf(ActivitySpec spec, ActivitySchema schema, IActivityCatalog catalog)
    {
        var element = new XElement(Wf + "If", RenderAttributes(spec, schema));
        element.Add(new XElement(Wf + "If.Then", RenderedChildren(spec.Children, catalog)));
        if (spec.Else is { Count: > 0 })
        {
            element.Add(new XElement(Wf + "If.Else", RenderedChildren(spec.Else, catalog)));
        }

        return element;
    }

    private static XElement RenderSwitch(ActivitySpec spec, ActivitySchema schema, IActivityCatalog catalog)
    {
        var element = new XElement(Wf + "Switch", RenderAttributes(spec, schema));
        foreach (var switchCase in spec.Cases ?? [])
        {
            element.Add(RenderKeyedBody(switchCase.Key, switchCase.Children, catalog));
        }

        if (spec.Default is { Count: > 0 })
        {
            element.Add(new XElement(Wf + "Switch.Default", RenderedChildren(spec.Default, catalog)));
        }

        return element;
    }

    private static XElement RenderKeyedBody(string key, List<ActivitySpec>? children, IActivityCatalog catalog)
    {
        var rendered = RenderedChildren(children, catalog).ToList();
        if (rendered.Count == 1)
        {
            rendered[0].Add(new XAttribute(X + "Key", key));
            return rendered[0];
        }

        var sequence = new XElement(Wf + "Sequence", new XAttribute(X + "Key", key));
        sequence.Add(rendered);
        return sequence;
    }

    private static XElement RenderInvoke(ActivitySpec spec, ActivitySchema schema, IActivityCatalog catalog)
    {
        var element = RenderGeneric(spec, schema, catalog);
        if (spec.Arguments is not { Count: > 0 })
        {
            return element;
        }

        var dictionary = new XElement(Scg + "Dictionary",
            new XAttribute(X + "TypeArguments", "x:String, Argument"));
        foreach (var argument in spec.Arguments)
        {
            var direction = NormalizeDirection(argument.Direction);
            var typeName = TypeToken.Render(string.IsNullOrWhiteSpace(argument.Type) ? "String" : argument.Type);
            var node = new XElement(Wf + direction,
                new XAttribute(X + "TypeArguments", typeName),
                new XAttribute(X + "Key", argument.Name));
            if (!string.IsNullOrEmpty(argument.Value))
            {
                node.Add(argument.Value);
            }

            dictionary.Add(node);
        }

        element.Add(new XElement(Ui + "InvokeWorkflowFile.Arguments", dictionary));
        return element;
    }

    private static string NormalizeDirection(string? direction)
    {
        if (string.IsNullOrWhiteSpace(direction) || direction.Equals("In", StringComparison.OrdinalIgnoreCase))
        {
            return "InArgument";
        }

        if (direction.Equals("Out", StringComparison.OrdinalIgnoreCase))
        {
            return "OutArgument";
        }

        return "InOutArgument";
    }

    private static XElement RenderTryCatch(ActivitySpec spec, ActivitySchema schema, IActivityCatalog catalog)
    {
        var element = new XElement(Wf + "TryCatch", RenderAttributes(spec, schema));
        element.Add(new XElement(Wf + "TryCatch.Try", RenderedChildren(spec.Children, catalog)));

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
                    RenderedChildren(catchSpec.Children, catalog))));
        }
        element.Add(catches);
        return element;
    }

    private static XElement RenderForEach(ActivitySpec spec, ActivitySchema schema, IActivityCatalog catalog)
    {
        var typeArgument = GetPropertyValue(spec, schema, "TypeArgument") ?? "Object";
        var itemName = GetPropertyValue(spec, schema, "ItemName") ?? "item";

        var element = new XElement(Wf + "ForEach", RenderAttributes(spec, schema, exclude: "ItemName"));
        element.Add(new XElement(Wf + "ActivityAction",
            new XAttribute(X + "TypeArguments", typeArgument),
            new XElement(Wf + "DelegateInArgument",
                new XAttribute(X + "TypeArguments", typeArgument),
                new XAttribute("Name", itemName)),
            RenderedChildren(spec.Children, catalog)));
        return element;
    }

    private static XElement RenderVariables(List<VariableSpec> variables) =>
        new(Wf + "Sequence.Variables", variables.Select(v =>
        {
            var element = new XElement(Wf + "Variable",
                new XAttribute(X + "TypeArguments", TypeToken.Render(v.Type)),
                new XAttribute("Name", v.Name));
            if (v.Default is not null)
            {
                element.Add(new XAttribute("Default", v.Default));
            }
            return element;
        }));

    private static void AddChildren(XElement element, List<ActivitySpec>? children, IActivityCatalog catalog)
    {
        foreach (var child in children ?? [])
        {
            element.Add(RenderElement(child, includeVariables: false, catalog));
        }
    }

    private static IEnumerable<XElement> RenderedChildren(List<ActivitySpec>? children, IActivityCatalog catalog) =>
        (children ?? []).Select(child => RenderElement(child, includeVariables: false, catalog));

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
    private static void AddFragmentNamespaceDeclarations(XElement root, ActivitySpec spec, IActivityCatalog catalog)
    {
        root.Add(new XAttribute("xmlns", Wf.NamespaceName));
        root.Add(new XAttribute(XNamespace.Xmlns + "x", X.NamespaceName));
        if (UsesNamespace(spec, Ui, catalog))
        {
            root.Add(new XAttribute(XNamespace.Xmlns + "ui", Ui.NamespaceName));
        }

        if (UsesInvokeArguments(spec))
        {
            root.Add(new XAttribute(XNamespace.Xmlns + "scg", Scg.NamespaceName));
        }
    }

    private static bool UsesNamespace(ActivitySpec spec, XNamespace ns, IActivityCatalog catalog)
    {
        if (catalog.TryGet(spec.Name, out var schema) && schema.XmlNamespace == ns.NamespaceName)
        {
            return true;
        }
        foreach (var child in spec.Children ?? [])
        {
            if (UsesNamespace(child, ns, catalog)) return true;
        }
        foreach (var child in spec.Else ?? [])
        {
            if (UsesNamespace(child, ns, catalog)) return true;
        }
        foreach (var child in spec.Default ?? [])
        {
            if (UsesNamespace(child, ns, catalog)) return true;
        }
        foreach (var switchCase in spec.Cases ?? [])
        {
            foreach (var child in switchCase.Children ?? [])
            {
                if (UsesNamespace(child, ns, catalog)) return true;
            }
        }
        foreach (var catchSpec in spec.Catches ?? [])
        {
            foreach (var child in catchSpec.Children ?? [])
            {
                if (UsesNamespace(child, ns, catalog)) return true;
            }
        }
        return false;
    }

    private static bool UsesInvokeArguments(ActivitySpec spec)
    {
        if (spec.Arguments is { Count: > 0 })
        {
            return true;
        }

        foreach (var child in spec.Children ?? [])
        {
            if (UsesInvokeArguments(child)) return true;
        }
        foreach (var child in spec.Else ?? [])
        {
            if (UsesInvokeArguments(child)) return true;
        }
        foreach (var child in spec.Default ?? [])
        {
            if (UsesInvokeArguments(child)) return true;
        }
        foreach (var switchCase in spec.Cases ?? [])
        {
            foreach (var child in switchCase.Children ?? [])
            {
                if (UsesInvokeArguments(child)) return true;
            }
        }
        foreach (var catchSpec in spec.Catches ?? [])
        {
            foreach (var child in catchSpec.Children ?? [])
            {
                if (UsesInvokeArguments(child)) return true;
            }
        }

        return false;
    }
}
