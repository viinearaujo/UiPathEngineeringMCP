using System.Xml.Linq;
using UiPath.Engineering.Mcp.Core.Models;

namespace UiPath.Engineering.Mcp.Core.Authoring;

public sealed partial class XamlRenderContext {
    internal XElement RenderAssign(ActivitySpec spec, ActivitySchema schema) {
        var typed = TypedAssign(spec);

        // Without a TypeArgument the spec asks for the non-generic object
        // form, which in VB is exactly what the attribute form already is.
        if (!typed && !_settings.IsCSharp) {
            var plain = new XElement(XamlNamespaces.Wf + "Assign", Attributes(spec, schema));
            AddChildren(plain, spec.Children);
            return plain;
        }

        if (!typed) {
            Warn("An Assign without \"TypeArgument\" renders the non-generic object form. Pass \"TypeArgument\": \"Int32\" (the variable's type) for the preferred Assign<T>, which surfaces type mismatches at validate time.");
        }

        var element = new XElement(XamlNamespaces.Wf + "Assign", Attributes(spec, schema));
        var token = DeclaredTypeArgument(spec) ?? "x:Object";
        AddArgumentProperty(element, schema, "To", PropertyValue(spec, "To"), token, ArgumentDirection.Out);
        AddArgumentProperty(element, schema, "Value", PropertyValue(spec, "Value"), token, ArgumentDirection.In);
        return element;
    }

    // InvokeWorkflowFile and InvokeCode both bind their parameters through an
    // <Arguments> scg:Dictionary keyed by name; neither takes an activity body.
    internal XElement RenderArguments(ActivitySpec spec, ActivitySchema schema) {
        var element = new XElement(Ns(schema) + schema.RenderName, Attributes(spec, schema));
        AddExpressionProperties(element, spec, schema);
        if (spec.Arguments is not { Count: > 0 }) {
            return element;
        }

        UseAlias("scg");
        var dictionary = new XElement(Scg + "Dictionary",
            new XAttribute(XamlNamespaces.X + "TypeArguments", "x:String, Argument"));
        foreach (var argument in spec.Arguments) {
            dictionary.Add(RenderArgumentMapping(argument));
        }

        element.Add(new XElement(Ns(schema) + schema.RenderName + ".Arguments", dictionary));
        return element;
    }

    internal XElement RenderArgumentMapping(ArgumentMappingSpec argument) {
        var direction = NormalizeDirection(argument.Direction);
        var token = TypeToken.Render(string.IsNullOrWhiteSpace(argument.Type) ? "String" : argument.Type);
        UseAliasesIn(token);

        var node = new XElement(XamlNamespaces.Wf + ArgumentElement(direction),
            new XAttribute(XamlNamespaces.X + "TypeArguments", token),
            new XAttribute(XamlNamespaces.X + "Key", argument.Name));
        AddBinding(node, argument.Value, token, direction);
        return node;
    }

    internal static ArgumentDirection NormalizeDirection(string? direction) {
        if (string.IsNullOrWhiteSpace(direction) || direction.Equals("In", StringComparison.OrdinalIgnoreCase)) {
            return ArgumentDirection.In;
        }

        return direction.Equals("Out", StringComparison.OrdinalIgnoreCase)
            ? ArgumentDirection.Out
            : ArgumentDirection.InOut;
    }

    internal static string ArgumentElement(ArgumentDirection direction) => direction switch {
        ArgumentDirection.Out => "OutArgument",
        ArgumentDirection.InOut => "InOutArgument",
        _ => "InArgument"
    };

    internal XElement RenderVariables(List<VariableSpec> variables) =>
        new(XamlNamespaces.Wf + "Sequence.Variables", variables.Select(RenderVariable));

    internal XElement RenderVariable(VariableSpec variable) {
        var token = TypeToken.Render(variable.Type);
        UseAliasesIn(token);
        var element = new XElement(XamlNamespaces.Wf + "Variable",
            new XAttribute(XamlNamespaces.X + "TypeArguments", token),
            new XAttribute("Name", variable.Name));
        if (variable.Default is null) {
            return element;
        }

        // A C# project cannot carry the default on the attribute: the
        // attribute parser builds a VisualBasicValue<T> from it. The typed
        // <Variable.Default> element with a CSharpValue is the C# form.
        if (_settings.IsCSharp) {
            element.Add(new XElement(XamlNamespaces.Wf + "Variable.Default",
                new XElement(XamlNamespaces.Wf + "CSharpValue",
                    new XAttribute(XamlNamespaces.X + "TypeArguments", token),
                    Unwrap(variable.Default))));
            return element;
        }

        element.Add(new XAttribute("Default", variable.Default));
        return element;
    }
}
