using System.Xml.Linq;
using UiPath.Engineering.Mcp.Core.Models;

namespace UiPath.Engineering.Mcp.Core.Authoring;

public sealed partial class XamlRenderContext {
    internal XElement RenderGeneric(ActivitySpec spec, ActivitySchema schema) {
        var element = new XElement(Ns(schema) + schema.RenderName, Attributes(spec, schema));
        AddExpressionProperties(element, spec, schema);
        AddChildren(element, spec.Children);
        return element;
    }

    // Dispatches a non-bespoke activity by the body shape its schema declares,
    // so the catalog — not a name list in the renderer — decides how the body
    // is wrapped.
    internal XElement RenderByBodyShape(ActivitySpec spec, ActivitySchema schema) {
        if (schema.Body is not { } body) {
            return RenderGeneric(spec, schema);
        }

        return body.Shape switch {
            BodyShape.Activity => RenderSingleBody(spec, schema),
            BodyShape.UntypedAction => RenderUntypedActionBody(spec, schema),
            BodyShape.TypedAction => RenderTypedActionBody(spec, schema),
            BodyShape.ActivityCollection => RenderCollectionBody(spec, schema),
            _ => RenderGeneric(spec, schema),
        };
    }

    internal XElement RenderIf(ActivitySpec spec, ActivitySchema schema) {
        var element = new XElement(XamlNamespaces.Wf + "If", Attributes(spec, schema));
        AddExpressionProperties(element, spec, schema);
        element.Add(new XElement(XamlNamespaces.Wf + "If.Then", WrappedBody(spec.Children, "Then")));
        if (spec.Else is { Count: > 0 }) {
            element.Add(new XElement(XamlNamespaces.Wf + "If.Else", WrappedBody(spec.Else, "Else")));
        }

        return element;
    }

    internal XElement RenderSwitch(ActivitySpec spec, ActivitySchema schema) {
        var element = new XElement(XamlNamespaces.Wf + "Switch", Attributes(spec, schema));
        AddExpressionProperties(element, spec, schema);
        foreach (var switchCase in spec.Cases ?? []) {
            element.Add(RenderKeyedBody(switchCase.Key, switchCase.Children));
        }

        if (spec.Default is { Count: > 0 }) {
            element.Add(new XElement(XamlNamespaces.Wf + "Switch.Default", WrappedBody(spec.Default, displayName: null)));
        }

        return element;
    }

    // A Switch case carries its literal on the wrapper Sequence, so the wrap
    // is always emitted (a bare keyed activity is legal XAML but is not the
    // drop zone Studio's designer expects).
    internal XElement RenderKeyedBody(string key, List<ActivitySpec>? children) {
        var sequence = WrappedBody(children, displayName: null);
        sequence.Add(new XAttribute(XamlNamespaces.X + "Key", key));
        return sequence;
    }

    internal XElement RenderTryCatch(ActivitySpec spec, ActivitySchema schema) {
        var element = new XElement(XamlNamespaces.Wf + "TryCatch", Attributes(spec, schema));
        AddExpressionProperties(element, spec, schema);
        element.Add(new XElement(XamlNamespaces.Wf + "TryCatch.Try", WrappedBody(spec.Children, "Try")));

        var catches = new XElement(XamlNamespaces.Wf + "TryCatch.Catches");
        foreach (var catchSpec in spec.Catches ?? []) {
            var exception = TypeToken.Render(catchSpec.Exception);
            UseAliasesIn(exception);
            catches.Add(new XElement(XamlNamespaces.Wf + "Catch",
                new XAttribute(XamlNamespaces.X + "TypeArguments", exception),
                new XElement(XamlNamespaces.Wf + "ActivityAction",
                    new XAttribute(XamlNamespaces.X + "TypeArguments", exception),
                    new XElement(XamlNamespaces.Wf + "ActivityAction.Argument",
                        new XElement(XamlNamespaces.Wf + "DelegateInArgument",
                            new XAttribute(XamlNamespaces.X + "TypeArguments", exception),
                            new XAttribute("Name", "ex"))),
                    WrappedBody(catchSpec.Children, "Catch"))));
        }

        element.Add(catches);
        return element;
    }

    // A scope activity whose body is an ActivityAction<T> held in a property
    // element (ForEachRow.Body, ExcelApplicationCard.Body, …). The iterator
    // type comes from the schema's body descriptor; the iterator name is the
    // spec's ItemName when it sets one, and the activity's fixed name otherwise.
    internal XElement RenderTypedActionBody(ActivitySpec spec, ActivitySchema schema) {
        var descriptor = schema.Body;
        var typeArgument = descriptor?.DelegateType switch {
            "System.Data.DataRow" => TypeToken.Render("DataRow"),
            "UiPath.Excel.IWorkbookQuickHandle" => "ue:IWorkbookQuickHandle",
            _ => DeclaredTypeArgument(spec) ?? "x:Object"
        };
        var itemName = PropertyValue(spec, "ItemName")
            ?? descriptor?.IteratorName
            ?? "item";

        UseAliasesIn(typeArgument);
        if (typeArgument.StartsWith("ue:", StringComparison.Ordinal)) {
            UseAlias("ue");
        }

        var element = new XElement(Ns(schema) + schema.RenderName, Attributes(spec, schema, exclude: "ItemName"));
        AddExpressionProperties(element, spec, schema);
        element.Add(new XElement(Ns(schema) + schema.RenderName + "." + (descriptor?.Property ?? "Body"),
            Body(typeArgument, itemName, spec.Children)));
        return element;
    }

    // An untyped ActivityAction body held in a property element
    // (RetryScope.ActivityBody): no DelegateInArgument, just the Action wrap.
    internal XElement RenderUntypedActionBody(ActivitySpec spec, ActivitySchema schema) {
        var descriptor = schema.Body;
        var element = new XElement(Ns(schema) + schema.RenderName, Attributes(spec, schema));
        AddExpressionProperties(element, spec, schema);
        element.Add(new XElement(Ns(schema) + schema.RenderName + "." + (descriptor?.Property ?? "ActivityBody"),
            new XElement(XamlNamespaces.Wf + "ActivityAction", WrappedBody(spec.Children, "Body"))));
        return element;
    }

    // A body property element holding a plain Sequence rather than an
    // ActivityAction (NApplicationCard.Body). The Rule-24 Sequence wrap is
    // that body.
    internal XElement RenderCollectionBody(ActivitySpec spec, ActivitySchema schema) {
        var descriptor = schema.Body;
        var element = new XElement(Ns(schema) + schema.RenderName, Attributes(spec, schema));
        AddExpressionProperties(element, spec, schema);
        element.Add(new XElement(Ns(schema) + schema.RenderName + "." + (descriptor?.Property ?? "Body"),
            WrappedBody(spec.Children, "Do")));
        return element;
    }

    // While / DoWhile take exactly one Activity body; the validator rejects a
    // second child, so the body here is always a single Sequence.
    internal XElement RenderSingleBody(ActivitySpec spec, ActivitySchema schema) {
        var element = new XElement(Ns(schema) + schema.RenderName, Attributes(spec, schema));
        AddExpressionProperties(element, spec, schema);
        element.Add(new XElement(Ns(schema) + schema.RenderName + ".Body",
            WrappedBody(spec.Children, "Body")));
        return element;
    }

    internal XElement WrappedBody(List<ActivitySpec>? children, string? displayName) {
        // A caller-supplied single Sequence owns its own variables, so it is
        // reused in place of the wrap rather than nested inside one.
        if (children is [{ } only] && IsSequence(only)) {
            return Element(only, includeVariables: only.Variables is { Count: > 0 });
        }

        var sequence = new XElement(XamlNamespaces.Wf + "Sequence");
        if (displayName is not null) {
            sequence.Add(new XAttribute("DisplayName", displayName));
        }

        AddChildren(sequence, children);
        return sequence;
    }

    // The ActivityAction + DelegateInArgument body of an iteration scope.
    internal XElement Body(string typeArgument, string itemName, List<ActivitySpec>? children) {
        UseAliasesIn(typeArgument);
        return new XElement(XamlNamespaces.Wf + "ActivityAction",
            new XAttribute(XamlNamespaces.X + "TypeArguments", typeArgument),
            new XElement(XamlNamespaces.Wf + "ActivityAction.Argument",
                new XElement(XamlNamespaces.Wf + "DelegateInArgument",
                    new XAttribute(XamlNamespaces.X + "TypeArguments", typeArgument),
                    new XAttribute("Name", itemName))),
            WrappedBody(children, "Body"));
    }

    internal bool IsSequence(ActivitySpec spec) =>
        _catalog.TryGet(spec.Name, out var schema)
        && string.Equals(schema.Name, "Sequence", StringComparison.OrdinalIgnoreCase);

    internal void AddChildren(XElement element, List<ActivitySpec>? children) {
        foreach (var child in children ?? []) {
            // A nested Sequence may declare its own variables (readability:
            // a variable is scoped to the block that uses it).
            element.Add(Element(child, includeVariables: IsSequence(child)));
        }
    }
}
