using System.Xml.Linq;
using UiPath.Engineering.Mcp.Core.Models;

namespace UiPath.Engineering.Mcp.Core.Authoring;

public sealed partial class XamlRenderContext {
    internal XElement RenderFlowchart(ActivitySpec spec, ActivitySchema schema) {
        var flowchart = spec.Flowchart!;
        var element = new XElement(Ns(schema) + schema.RenderName, Attributes(spec, schema));
        element.Add(new XAttribute(XNamespace.Xmlns + "av", ViewStateNamespaces.Av));

        if (!string.IsNullOrWhiteSpace(flowchart.Start)) {
            element.Add(new XElement(XamlNamespaces.Wf + "Flowchart.StartNode", Reference(flowchart.Start!)));
        }

        foreach (var node in flowchart.Nodes ?? []) {
            element.Add(RenderFlowNode(node, schema));
        }

        return element;
    }

    internal XElement RenderFlowNode(FlowNodeSpec node, ActivitySchema schema) {
        return (node.Type ?? "Step").Trim().ToLowerInvariant() switch {
            "decision" => RenderFlowDecision(node),
            "switch" => RenderFlowSwitch(node),
            _ => RenderFlowStep(node),
        };
    }

    internal XElement RenderFlowStep(FlowNodeSpec node) {
        var element = new XElement(XamlNamespaces.Wf + "FlowStep",
            new XAttribute(XamlNamespaces.X + "Name", ReferenceName(node.Id)));
        element.Add(ShapeViewState(node.ShapeLocation, node.ShapeSize));
        if (node.Activity is { } activity) {
            element.Add(Element(activity, includeVariables: IsSequence(activity)));
        }

        if (!string.IsNullOrWhiteSpace(node.Next)) {
            element.Add(new XElement(XamlNamespaces.Wf + "FlowStep.Next", Reference(node.Next!)));
        }

        return element;
    }

    internal XElement RenderFlowDecision(FlowNodeSpec node) {
        var element = new XElement(XamlNamespaces.Wf + "FlowDecision", new XAttribute(XamlNamespaces.X + "Name", ReferenceName(node.Id)));
        element.Add(ShapeViewState(node.ShapeLocation, node.ShapeSize));
        if (!string.IsNullOrWhiteSpace(node.DisplayName)) {
            element.Add(new XAttribute("DisplayName", node.DisplayName!));
        }

        AddCondition(element, "FlowDecision.Condition", node.Condition);
        if (node.True is { Length: > 0 }) {
            element.Add(new XElement(XamlNamespaces.Wf + "FlowDecision.True", Reference(node.True)));
        }

        if (node.False is { Length: > 0 }) {
            element.Add(new XElement(XamlNamespaces.Wf + "FlowDecision.False", Reference(node.False)));
        }

        return element;
    }

    internal XElement RenderFlowSwitch(FlowNodeSpec node) {
        var token = string.IsNullOrWhiteSpace(node.TypeArgument) ? "x:Object" : TypeToken.Render(node.TypeArgument!);
        UseAliasesIn(token);
        var element = new XElement(XamlNamespaces.Wf + "FlowSwitch",
            new XAttribute(XamlNamespaces.X + "Name", ReferenceName(node.Id)),
            new XAttribute(XamlNamespaces.X + "TypeArguments", token));
        element.Add(ShapeViewState(node.ShapeLocation, node.ShapeSize));
        if (!string.IsNullOrWhiteSpace(node.DisplayName)) {
            element.Add(new XAttribute("DisplayName", node.DisplayName!));
        }

        AddCondition(element, "FlowSwitch.Expression", node.Expression);
        foreach (var switchCase in node.Cases ?? []) {
            var target = new XElement(XamlNamespaces.Wf + "FlowSwitchCase");
            target.Add(new XAttribute(XamlNamespaces.X + "Key", switchCase.Key));
            if (switchCase.Target is { Length: > 0 }) {
                target.Add(Reference(switchCase.Target));
            }

            element.Add(target);
        }

        if (node.Default is { Length: > 0 }) {
            element.Add(new XElement(XamlNamespaces.Wf + "FlowSwitch.Default", Reference(node.Default)));
        }

        return element;
    }

    internal void AddCondition(XElement owner, string propertyName, string? expression) {
        if (string.IsNullOrWhiteSpace(expression)) {
            return;
        }

        var known = propertyName.Contains("Condition", StringComparison.Ordinal)
            ? ("x:Boolean", ArgumentDirection.In)
            : (DeclaredTypeArgumentFromExpression(expression) ?? "x:Object", ArgumentDirection.In);
        UseAliasesIn(known.Item1);
        var argument = new XElement(XamlNamespaces.Wf + "InArgument", new XAttribute(XamlNamespaces.X + "TypeArguments", known.Item1));
        AddBinding(argument, expression, known.Item1, known.Item2);
        owner.Add(new XElement(XamlNamespaces.Wf + propertyName, argument));
    }

    // A FlowSwitch.Expression's type is its own TypeArgument, already rendered
    // as the activity's x:TypeArguments; the argument repeats it.
    internal static string? DeclaredTypeArgumentFromExpression(string _) => null;

    // A StateMachine's States are its direct children; each transition points
    // at its destination with <x:Reference>.
    internal XElement RenderStateMachine(ActivitySpec spec, ActivitySchema schema) {
        var machine = spec.StateMachine!;
        var element = new XElement(Ns(schema) + schema.RenderName, Attributes(spec, schema));
        element.Add(new XAttribute(XNamespace.Xmlns + "av", ViewStateNamespaces.Av));

        if (!string.IsNullOrWhiteSpace(machine.Start)) {
            element.Add(new XAttribute("InitialState", $"{{x:Reference {ReferenceName(machine.Start!)}}}"));
        }

        foreach (var state in machine.States ?? []) {
            element.Add(RenderState(state));
        }

        return element;
    }

    internal XElement RenderState(StateSpec state) {
        var kind = string.IsNullOrWhiteSpace(state.Kind) ? "State" : state.Kind!;
        var element = new XElement(XamlNamespaces.Wf + kind,
            new XAttribute(XamlNamespaces.X + "Name", ReferenceName(state.Id)));
        if (!string.IsNullOrWhiteSpace(state.DisplayName)) {
            element.Add(new XAttribute("DisplayName", state.DisplayName!));
        }

        element.Add(ShapeViewState(state.ShapeLocation, state.ShapeSize));

        if (state.Activities is { Count: > 0 }) {
            var entry = new XElement(XamlNamespaces.Wf + kind + ".Entry",
                WrappedBody(state.Activities, displayName: null));
            element.Add(entry);
        }

        foreach (var transition in state.Transitions ?? []) {
            element.Add(RenderTransition(transition));
        }

        return element;
    }

    internal XElement RenderTransition(StateTransitionSpec transition) {
        var element = new XElement(XamlNamespaces.Wf + "Transition");
        if (!string.IsNullOrWhiteSpace(transition.DisplayName)) {
            element.Add(new XAttribute("DisplayName", transition.DisplayName!));
        }

        element.Add(ShapeViewState(transition.ShapeLocation, transition.ShapeSize));
        if (transition.To is { Length: > 0 }) {
            element.Add(new XElement(XamlNamespaces.Wf + "Transition.To", Reference(transition.To)));
        }

        if (!string.IsNullOrWhiteSpace(transition.Condition)) {
            var argument = new XElement(XamlNamespaces.Wf + "InArgument", new XAttribute(XamlNamespaces.X + "TypeArguments", "x:Boolean"));
            AddBinding(argument, transition.Condition, "x:Boolean", ArgumentDirection.In);
            element.Add(new XElement(XamlNamespaces.Wf + "Transition.Condition", argument));
        }

        return element;
    }

    // <x:Reference>__ReferenceIDn</x:Reference>. Node ids are normalized so a
    // caller may pass either the raw id or a namespaced one.
    internal static XElement Reference(string target) =>
        new(XamlNamespaces.X + "Reference", ReferenceName(target));

    internal static string ReferenceName(string target) {
        var name = target.Trim();
        return name.StartsWith("__ReferenceID", StringComparison.Ordinal) ? name : "__ReferenceID" + name;
    }

    // Rule 20: ShapeLocation + ShapeSize are mandatory on every node so Studio
    // does not stack them at (0,0). Missing coordinates get a deterministic
    // default that keeps the node visible.
    internal XElement ShapeViewState(string? location, string? size) =>
        new(XamlNamespaces.Sap + "WorkflowViewStateService.ViewState",
            new XElement(Scg + "Dictionary",
                new XAttribute(XamlNamespaces.X + "TypeArguments", "x:String, x:Object"),
                new XElement(ViewStateNamespaces.PointName,
                    new XAttribute(XamlNamespaces.X + "Key", "ShapeLocation"),
                    location ?? "170,110"),
                new XElement(ViewStateNamespaces.SizeName,
                    new XAttribute(XamlNamespaces.X + "Key", "ShapeSize"),
                    size ?? "262,60")));
}
