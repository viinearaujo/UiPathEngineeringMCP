using System.Xml.Linq;
using UiPath.Engineering.Mcp.Core.Models;
using UiPath.Engineering.Mcp.Core.Parsing;

namespace UiPath.Engineering.Mcp.Core.Authoring;

public sealed record XamlBuildResult(bool Success, string? Xaml, List<ToolError> Errors) {
    /// <summary>
    /// Non-fatal notes about the rendered XAML: properties the schema does not
    /// know about, expression argument types that had to be assumed, and spec
    /// fields the target project ignores.
    /// </summary>
    public List<string> Warnings { get; init; } = [];
}

// Renders a validated ActivitySpec into Studio-valid XAML.
//
// Expression form follows the project's expression language. A VisualBasic
// project keeps attribute-form values with [bracket] shorthand. A CSharp project
// renders every non-literal expression as a typed <InArgument>/<OutArgument>
// property element holding <CSharpValue> (read) or <CSharpReference> (write),
// because a non-literal attribute value deserializes as VisualBasicValue<T>
// regardless of expressionLanguage and then fails at runtime with "JIT
// compilation is disabled for non-Legacy projects". Attribute form is kept in a
// C# project only for values with a direct type converter: enums, numbers,
// booleans, TimeSpan literals, {x:Null} and plain string literals.
//
// Container bodies use their canonical shape (.Body / .ActivityBody with
// ActivityAction + DelegateInArgument) and every body or branch slot is wrapped
// in a <Sequence>: Studio's designer expects the wrap as a drop zone, and
// validate/build accept the bare form so nothing else catches a missing one.
//
// If: Children = Then branch; Else = Else branch (omitted when empty).
// Switch: Cases keyed by literal; Default = fallback branch.
// While / DoWhile: Children = the single Activity body.
// ForEach / ForEachRow: Children = the per-iteration body.
// RetryScope: Children = the ActivityBody (the activities to attempt).
// InvokeWorkflowFile / InvokeCode: Arguments = In/Out/InOut bindings; no body.
public static class XamlBuilder {
    public static XamlBuildResult RenderFragment(ActivitySpec spec) =>
        RenderFragment(spec, ActivityCatalog.Fallback, null);

    public static XamlBuildResult RenderFragment(ActivitySpec spec, IActivityCatalog catalog) =>
        RenderFragment(spec, catalog, null);

    public static XamlBuildResult RenderFragment(ActivitySpec spec, IActivityCatalog catalog, ProjectXamlSettings? settings) {
        var renderer = new XamlRenderContext(catalog, settings);
        var errors = renderer.Validate(spec);
        if (errors.Count > 0) {
            return new XamlBuildResult(false, null, errors);
        }

        try {
            var element = renderer.Element(spec, includeVariables: true);
            renderer.DeclareFragmentNamespaces(element, spec);
            return new XamlBuildResult(true, element.ToString(), []) { Warnings = renderer.Warnings };
        } catch (Exception ex) {
            return new XamlBuildResult(false, null, [RenderFailed(ex)]);
        }
    }

    public static XamlBuildResult RenderWorkflowFile(ActivitySpec spec, string xamlClassName) =>
        RenderWorkflowFile(spec, xamlClassName, ActivityCatalog.Fallback, null);

    public static XamlBuildResult RenderWorkflowFile(ActivitySpec spec, string xamlClassName, IActivityCatalog catalog) =>
        RenderWorkflowFile(spec, xamlClassName, catalog, null);

    public static XamlBuildResult RenderWorkflowFile(
        ActivitySpec spec, string xamlClassName, IActivityCatalog catalog, ProjectXamlSettings? settings) {
        var renderer = new XamlRenderContext(catalog, settings);
        var errors = renderer.Validate(spec);
        if (errors.Count > 0) {
            return new XamlBuildResult(false, null, errors);
        }

        try {
            var root = new XElement(XamlNamespaces.Wf + "Activity",
                new XAttribute(XamlNamespaces.Mc + "Ignorable", "sap sap2010"),
                new XAttribute(XamlNamespaces.X + "Class", xamlClassName),
                new XAttribute("xmlns", XamlNamespaces.Wf.NamespaceName),
                new XAttribute(XNamespace.Xmlns + "mc", XamlNamespaces.Mc.NamespaceName),
                new XAttribute(XNamespace.Xmlns + "sap", XamlNamespaces.Sap.NamespaceName),
                new XAttribute(XNamespace.Xmlns + "sap2010", XamlNamespaces.Sap2010.NamespaceName),
                new XAttribute(XNamespace.Xmlns + "x", XamlNamespaces.X.NamespaceName));

            if (UsesDiagram(spec)) {
                root.Add(new XAttribute(XNamespace.Xmlns + "av", ViewStateNamespaces.Av));
            }

            var effective = spec;
            if (spec.Variables is { Count: > 0 } && !string.Equals(spec.Name, "Sequence", StringComparison.OrdinalIgnoreCase)) {
                effective = new ActivitySpec {
                    Name = "Sequence",
                    Variables = spec.Variables,
                    Children = [WithoutVariables(spec)],
                };
            }

            if (renderer.Members(spec) is { } members) {
                root.Add(members);
            }

            if (renderer.LanguageBlock(spec) is { } languageBlock) {
                root.Add(languageBlock);
            }

            root.Add(renderer.Element(effective, includeVariables: true));
            renderer.DeclareNamespaces(root, effective);

            var document = new XDocument(root);
            XamlViewStateEmitter.Apply(document, settings?.CoreAssembly);
            XamlViewStateRenderer.DeclareRootNamespace(document.Root!, settings?.CoreAssembly);

            var xaml = document.Root!.ToString();

            var parsed = new XamlWorkflowParser().Parse(xamlClassName + ".xaml", xamlClassName + ".xaml", xaml);
            if (parsed.HasParseError) {
                return new XamlBuildResult(false, null, [new ToolError(
                    ToolErrorCodes.XamlRoundtripFailed,
                    $"The generated XAML failed to parse when read back: {parsed.ParseError}",
                    "The spec validated but produced malformed XAML; simplify the spec or report this as an authoring bug.")]);
            }

            return new XamlBuildResult(true, xaml, []) { Warnings = renderer.Warnings };
        } catch (Exception ex) {
            return new XamlBuildResult(false, null, [RenderFailed(ex)]);
        }
    }

    private static ToolError RenderFailed(Exception ex) => new(
        ToolErrorCodes.XamlRenderFailed,
        $"Failed to render the spec as XAML: {ex.Message}",
        "Fix the offending property values and retry; run validate_activity_spec for detailed property guidance.");

    private static bool UsesDiagram(ActivitySpec spec) {
        if (spec.Flowchart is not null || spec.StateMachine is not null) {
            return true;
        }

        return (spec.Children ?? []).Any(UsesDiagram)
            || (spec.Else ?? []).Any(UsesDiagram)
            || (spec.Default ?? []).Any(UsesDiagram)
            || (spec.Cases ?? []).Any(c => (c.Children ?? []).Any(UsesDiagram))
            || (spec.Catches ?? []).Any(c => (c.Children ?? []).Any(UsesDiagram));
    }

    private static ActivitySpec WithoutVariables(ActivitySpec spec) => new() {
        Name = spec.Name,
        Properties = spec.Properties,
        Children = spec.Children,
        Catches = spec.Catches,
        Else = spec.Else,
        Cases = spec.Cases,
        Default = spec.Default,
        Arguments = spec.Arguments,
    };
}
