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
    // Renders a validated spec as a fragment (elements only, no <Activity> root).
    public static XamlBuildResult RenderFragment(ActivitySpec spec) =>
        RenderFragment(spec, ActivityCatalog.Fallback, null);

    public static XamlBuildResult RenderFragment(ActivitySpec spec, IActivityCatalog catalog) =>
        RenderFragment(spec, catalog, null);

    public static XamlBuildResult RenderFragment(ActivitySpec spec, IActivityCatalog catalog, ProjectXamlSettings? settings) {
        var renderer = new Renderer(catalog, settings);
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

    // Renders a full workflow file: <Activity x:Class="…" …>{fragment}</Activity>.
    // Validates first (never renders an invalid spec), then round-trips the output
    // through XamlWorkflowParser to prove the file parses.
    public static XamlBuildResult RenderWorkflowFile(ActivitySpec spec, string xamlClassName) =>
        RenderWorkflowFile(spec, xamlClassName, ActivityCatalog.Fallback, null);

    public static XamlBuildResult RenderWorkflowFile(ActivitySpec spec, string xamlClassName, IActivityCatalog catalog) =>
        RenderWorkflowFile(spec, xamlClassName, catalog, null);

    public static XamlBuildResult RenderWorkflowFile(
        ActivitySpec spec, string xamlClassName, IActivityCatalog catalog, ProjectXamlSettings? settings) {
        var renderer = new Renderer(catalog, settings);
        var errors = renderer.Validate(spec);
        if (errors.Count > 0) {
            return new XamlBuildResult(false, null, errors);
        }

        try {
            var root = new XElement(Renderer.Wf + "Activity",
                new XAttribute(Renderer.Mc + "Ignorable", "sap sap2010"),
                new XAttribute(Renderer.X + "Class", xamlClassName),
                new XAttribute("xmlns", Renderer.Wf.NamespaceName),
                new XAttribute(XNamespace.Xmlns + "mc", Renderer.Mc.NamespaceName),
                new XAttribute(XNamespace.Xmlns + "sap", Renderer.Sap.NamespaceName),
                new XAttribute(XNamespace.Xmlns + "sap2010", Renderer.Sap2010.NamespaceName),
                new XAttribute(XNamespace.Xmlns + "x", Renderer.X.NamespaceName));

            // Root variables live on a Sequence; wrap a non-Sequence root so they
            // still have a valid home.
            var effective = spec;
            if (spec.Variables is { Count: > 0 } && !string.Equals(spec.Name, "Sequence", StringComparison.OrdinalIgnoreCase)) {
                effective = new ActivitySpec {
                    Name = "Sequence",
                    Variables = spec.Variables,
                    Children = [WithoutVariables(spec)],
                };
            }

            // The expression-language block precedes the body, matching the file
            // anatomy Studio writes (x:Members, language settings, imports, body).
            if (renderer.Members(spec) is { } members) {
                root.Add(members);
            }

            if (renderer.LanguageBlock(spec) is { } languageBlock) {
                root.Add(languageBlock);
            }

            root.Add(renderer.Element(effective, includeVariables: true));
            renderer.DeclareNamespaces(root, effective);

            var xaml = root.ToString();

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

    // One renderer instance carries the project settings, the catalog and the
    // state gathered while rendering (warnings, xmlns aliases actually used) so
    // the root element declares exactly the namespaces the body needs.
    internal sealed class Renderer {
        internal static readonly XNamespace Wf = "http://schemas.microsoft.com/netfx/2009/xaml/activities";
        internal static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";
        internal static readonly XNamespace Mc = "http://schemas.openxmlformats.org/markup-compatibility/2006";
        internal static readonly XNamespace Sap = "http://schemas.microsoft.com/netfx/2009/xaml/activities/presentation";
        internal static readonly XNamespace Sap2010 = "http://schemas.microsoft.com/netfx/2010/xaml/activities/presentation";
        internal static readonly XNamespace Ui = "http://schemas.uipath.com/workflow/activities";
        internal static readonly XNamespace Uix = "http://schemas.uipath.com/workflow/activities/uix";
        internal static readonly XNamespace ModernExcel = "clr-namespace:UiPath.Excel.Activities.Business;assembly=UiPath.Excel.Activities";
        internal static readonly XNamespace ModernExcelModel = "clr-namespace:UiPath.Excel;assembly=UiPath.Excel.Activities";

        // Namespace imports Studio writes into a new C# workflow. A spec that
        // supplies its own 'imports' list replaces this set.
        private static readonly string[] DefaultCSharpImports = [
            "System",
            "System.Activities",
            "System.Collections.Generic",
            "System.Data",
            "System.Linq",
            "System.Threading.Tasks",
            "UiPath.Core",
            "UiPath.Core.Activities"
        ];

        private const int MaxWarnings = 20;

        private readonly IActivityCatalog _catalog;
        private readonly ProjectXamlSettings _settings;
        private readonly HashSet<string> _aliases = new(StringComparer.Ordinal);
        private readonly HashSet<string> _warned = new(StringComparer.Ordinal);

        internal Renderer(IActivityCatalog catalog, ProjectXamlSettings? settings) {
            _catalog = catalog;
            _settings = settings ?? ProjectXamlSettings.Default;
            var coreAssembly = _settings.CoreAssembly;
            S = XNamespace.Get($"clr-namespace:System;assembly={coreAssembly}");
            Sc = XNamespace.Get($"clr-namespace:System.Collections;assembly={coreAssembly}");
            Scg = XNamespace.Get($"clr-namespace:System.Collections.Generic;assembly={coreAssembly}");
            Sco = XNamespace.Get($"clr-namespace:System.Collections.ObjectModel;assembly={coreAssembly}");
            // System.Data resolves through type forwarding on both targets.
            Sd = XNamespace.Get("clr-namespace:System.Data;assembly=System.Data");
        }

        private XNamespace S { get; }
        private XNamespace Sc { get; }
        private XNamespace Scg { get; }
        private XNamespace Sco { get; }
        private XNamespace Sd { get; }

        internal List<string> Warnings { get; } = [];

        internal List<ToolError> Validate(ActivitySpec spec) => SpecValidator.Validate(spec, _catalog, _settings);

        // ---- root-level blocks -------------------------------------------------

        // <x:Members> with one <x:Property> per declared workflow argument. A
        // spec-built workflow with no arguments renders no Members block (Studio
        // emits an empty one; either is loadable).
        internal XElement? Members(ActivitySpec spec) {
            if (spec.WorkflowArguments is not { Count: > 0 }) {
                return null;
            }

            var members = new XElement(X + "Members");
            foreach (var argument in spec.WorkflowArguments) {
                var token = TypeToken.Render(string.IsNullOrWhiteSpace(argument.Type) ? "String" : argument.Type);
                UseAliasesIn(token);
                members.Add(new XElement(X + "Property",
                    new XAttribute("Name", argument.Name),
                    new XAttribute("Type", $"{ArgumentWrapper(argument.Direction)}({token})")));
            }

            return members;
        }

        private static string ArgumentWrapper(string? direction) =>
            (direction ?? "In").Trim().ToLowerInvariant() switch {
                "out" => "OutArgument",
                "inout" or "in/out" => "InOutArgument",
                _ => "InArgument"
            };

        // <TextExpression.NamespacesForImplementation> for a C# project — the only
        // place a C# XAML workflow declares its expression imports — and
        // <VisualBasic.Settings> for a VB project, where imports are configured in
        // project.json instead.
        internal XElement? LanguageBlock(ActivitySpec spec) {
            if (!_settings.IsCSharp) {
                if (spec.Imports is { Count: > 0 }) {
                    Warn("The spec's 'imports' list is ignored for a VisualBasic project; VB namespace imports are configured in project.json (designOptions).");
                }

                return new XElement(Wf + "VisualBasic.Settings", new XElement(X + "Null"));
            }

            var imports = spec.Imports is { Count: > 0 } ? spec.Imports : (IReadOnlyList<string>)DefaultCSharpImports;
            var collection = new XElement(Sco + "Collection", new XAttribute(X + "TypeArguments", "x:String"));
            foreach (var import in imports) {
                if (!string.IsNullOrWhiteSpace(import)) {
                    collection.Add(new XElement(X + "String", import.Trim()));
                }
            }

            UseAlias("sco");
            return new XElement(Wf + "TextExpression.NamespacesForImplementation", collection);
        }

        // A fragment has no <Activity> root to carry declarations, so its root
        // element declares the namespaces it needs (appended after the property
        // attributes).
        internal void DeclareFragmentNamespaces(XElement root, ActivitySpec spec) {
            root.Add(new XAttribute("xmlns", Wf.NamespaceName));
            root.Add(new XAttribute(XNamespace.Xmlns + "x", X.NamespaceName));
            if (UsesAnnotation(spec)) {
                root.Add(new XAttribute(XNamespace.Xmlns + "sap2010", Sap2010.NamespaceName));
            }

            DeclareNamespaces(root, spec);
        }

        private static bool UsesAnnotation(ActivitySpec spec) {
            if (!string.IsNullOrWhiteSpace(spec.Annotation)) {
                return true;
            }

            return (spec.Children ?? []).Any(UsesAnnotation)
                || (spec.Else ?? []).Any(UsesAnnotation)
                || (spec.Default ?? []).Any(UsesAnnotation)
                || (spec.Cases ?? []).Any(c => (c.Children ?? []).Any(UsesAnnotation))
                || (spec.Catches ?? []).Any(c => (c.Children ?? []).Any(UsesAnnotation));
        }

        internal void DeclareNamespaces(XElement root, ActivitySpec spec) {
            var aliases = new HashSet<string>(_aliases, StringComparer.Ordinal);
            CollectRequiredAliases(spec, aliases);

            foreach (var alias in aliases.OrderBy(a => a, StringComparer.Ordinal)) {
                if (AliasNamespace(alias) is { } ns) {
                    root.Add(new XAttribute(XNamespace.Xmlns + alias, ns.NamespaceName));
                }
            }

            // The catalog aliases that are not s:/sd:/scg:/… : the UiPath ui: prefix,
            // the UIA uix: prefix, and the modern Excel ueab: clr-namespace.
            if (UsesNamespace(spec, Ui) && root.Attribute(XNamespace.Xmlns + "ui") is null) {
                root.Add(new XAttribute(XNamespace.Xmlns + "ui", Ui.NamespaceName));
            }

            if (UsesNamespace(spec, Uix) && root.Attribute(XNamespace.Xmlns + "uix") is null) {
                root.Add(new XAttribute(XNamespace.Xmlns + "uix", Uix.NamespaceName));
            }

            if (UsesNamespace(spec, ModernExcel) && root.Attribute(XNamespace.Xmlns + "ueab") is null) {
                root.Add(new XAttribute(XNamespace.Xmlns + "ueab", ModernExcel.NamespaceName));
            }
        }

        // Type tokens are rendered while children are built, but a namespace
        // declaration must appear on the outermost element that uses it. This
        // pass mirrors the token/alias rules over the same filter so the
        // declared set matches the set actually emitted.
        private void CollectRequiredAliases(ActivitySpec spec, HashSet<string> aliases) {
            if (_catalog.TryGet(spec.Name, out var schema)) {
                foreach (var property in schema.Properties) {
                    var value = PropertyValue(spec, property.Name);
                    if (value is null) {
                        continue;
                    }

                    if (property.Kind == PropertyKind.TypeArgument) {
                        foreach (var alias in TypeToken.AliasesIn(TypeToken.Render(value))) {
                            aliases.Add(alias);
                        }
                    } else if (property.Kind == PropertyKind.Expression
                        && ActivityCatalog.ExpressionArgument(schema.Name, property.Name) is { Token.Length: > 0 } known) {
                        foreach (var alias in TypeToken.AliasesIn(known.Token)) {
                            aliases.Add(alias);
                        }
                    }
                }
            }

            foreach (var variable in spec.Variables ?? []) {
                foreach (var alias in TypeToken.AliasesIn(TypeToken.Render(variable.Type))) {
                    aliases.Add(alias);
                }
            }

            foreach (var child in spec.Children ?? []) {
                CollectRequiredAliases(child, aliases);
            }

            foreach (var child in spec.Else ?? []) {
                CollectRequiredAliases(child, aliases);
            }

            foreach (var child in spec.Default ?? []) {
                CollectRequiredAliases(child, aliases);
            }

            foreach (var switchCase in spec.Cases ?? []) {
                foreach (var child in switchCase.Children ?? []) {
                    CollectRequiredAliases(child, aliases);
                }
            }

            foreach (var catchSpec in spec.Catches ?? []) {
                foreach (var alias in TypeToken.AliasesIn(TypeToken.Render(catchSpec.Exception))) {
                    aliases.Add(alias);
                }

                foreach (var child in catchSpec.Children ?? []) {
                    CollectRequiredAliases(child, aliases);
                }
            }
        }

        private XNamespace? AliasNamespace(string alias) => alias switch {
            "s" => S,
            "sc" => Sc,
            "scg" => Scg,
            "sco" => Sco,
            "sd" => Sd,
            "ue" => ModernExcelModel,
            "ui" => Ui,
            "uix" => Uix,
            "ueab" => ModernExcel,
            _ => null
        };

        private void UseAlias(string alias) => _aliases.Add(alias);

        private void UseAliasesIn(string? typeArguments) {
            foreach (var alias in TypeToken.AliasesIn(typeArguments)) {
                if (AliasNamespace(alias) is not null) {
                    UseAlias(alias);
                }
            }
        }

        // ---- elements ----------------------------------------------------------

        internal XElement Element(ActivitySpec spec, bool includeVariables) {
            if (!_catalog.TryGet(spec.Name, out var schema)) {
                throw new InvalidOperationException($"Unknown activity \"{spec.Name}\".");
            }

            var element = schema.Name switch {
                "If" => RenderIf(spec, schema),
                "Switch" => RenderSwitch(spec, schema),
                "TryCatch" => RenderTryCatch(spec, schema),
                "Assign" => RenderAssign(spec, schema),
                _ when ActivityCatalog.AcceptsArgumentDictionary(schema.Name) => RenderArguments(spec, schema),
                _ => RenderByBodyShape(spec, schema),
            };

            if (includeVariables && spec.Variables is { Count: > 0 }) {
                element.AddFirst(RenderVariables(spec.Variables));
            }

            AddAnnotation(element, spec);
            return element;
        }

        // Studio's per-activity annotation: sap2010:Annotation.AnnotationText. The
        // Outline pane and the designer tooltip surface it; ProjectGapAnalyzer
        // flags its absence on user workflows, so the builder must be able to write
        // one. A root annotation is also the workflow description the parser reads.
        private static void AddAnnotation(XElement element, ActivitySpec spec) {
            if (string.IsNullOrWhiteSpace(spec.Annotation)) {
                return;
            }

            element.Add(new XAttribute(Sap2010 + "Annotation.AnnotationText", spec.Annotation));
        }

        private XElement RenderGeneric(ActivitySpec spec, ActivitySchema schema) {
            var element = new XElement(Ns(schema) + schema.RenderName, Attributes(spec, schema));
            AddExpressionProperties(element, spec, schema);
            AddChildren(element, spec.Children);
            return element;
        }

        // Dispatches a non-bespoke activity by the body shape its schema declares,
        // so the catalog — not a name list in the renderer — decides how the body
        // is wrapped.
        private XElement RenderByBodyShape(ActivitySpec spec, ActivitySchema schema) {
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

        private XElement RenderIf(ActivitySpec spec, ActivitySchema schema) {
            var element = new XElement(Wf + "If", Attributes(spec, schema));
            AddExpressionProperties(element, spec, schema);
            element.Add(new XElement(Wf + "If.Then", WrappedBody(spec.Children, "Then")));
            if (spec.Else is { Count: > 0 }) {
                element.Add(new XElement(Wf + "If.Else", WrappedBody(spec.Else, "Else")));
            }

            return element;
        }

        private XElement RenderSwitch(ActivitySpec spec, ActivitySchema schema) {
            var element = new XElement(Wf + "Switch", Attributes(spec, schema));
            AddExpressionProperties(element, spec, schema);
            foreach (var switchCase in spec.Cases ?? []) {
                element.Add(RenderKeyedBody(switchCase.Key, switchCase.Children));
            }

            if (spec.Default is { Count: > 0 }) {
                element.Add(new XElement(Wf + "Switch.Default", WrappedBody(spec.Default, displayName: null)));
            }

            return element;
        }

        // A Switch case carries its literal on the wrapper Sequence, so the wrap
        // is always emitted (a bare keyed activity is legal XAML but is not the
        // drop zone Studio's designer expects).
        private XElement RenderKeyedBody(string key, List<ActivitySpec>? children) {
            var sequence = WrappedBody(children, displayName: null);
            sequence.Add(new XAttribute(X + "Key", key));
            return sequence;
        }

        private XElement RenderTryCatch(ActivitySpec spec, ActivitySchema schema) {
            var element = new XElement(Wf + "TryCatch", Attributes(spec, schema));
            AddExpressionProperties(element, spec, schema);
            element.Add(new XElement(Wf + "TryCatch.Try", WrappedBody(spec.Children, "Try")));

            var catches = new XElement(Wf + "TryCatch.Catches");
            foreach (var catchSpec in spec.Catches ?? []) {
                var exception = TypeToken.Render(catchSpec.Exception);
                UseAliasesIn(exception);
                catches.Add(new XElement(Wf + "Catch",
                    new XAttribute(X + "TypeArguments", exception),
                    new XElement(Wf + "ActivityAction",
                        new XAttribute(X + "TypeArguments", exception),
                        new XElement(Wf + "ActivityAction.Argument",
                            new XElement(Wf + "DelegateInArgument",
                                new XAttribute(X + "TypeArguments", exception),
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
        private XElement RenderTypedActionBody(ActivitySpec spec, ActivitySchema schema) {
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
        private XElement RenderUntypedActionBody(ActivitySpec spec, ActivitySchema schema) {
            var descriptor = schema.Body;
            var element = new XElement(Ns(schema) + schema.RenderName, Attributes(spec, schema));
            AddExpressionProperties(element, spec, schema);
            element.Add(new XElement(Ns(schema) + schema.RenderName + "." + (descriptor?.Property ?? "ActivityBody"),
                new XElement(Wf + "ActivityAction", WrappedBody(spec.Children, "Body"))));
            return element;
        }

        // A body property element holding a plain Sequence rather than an
        // ActivityAction (NApplicationCard.Body). The Rule-24 Sequence wrap is
        // that body.
        private XElement RenderCollectionBody(ActivitySpec spec, ActivitySchema schema) {
            var descriptor = schema.Body;
            var element = new XElement(Ns(schema) + schema.RenderName, Attributes(spec, schema));
            AddExpressionProperties(element, spec, schema);
            element.Add(new XElement(Ns(schema) + schema.RenderName + "." + (descriptor?.Property ?? "Body"),
                WrappedBody(spec.Children, "Do")));
            return element;
        }

        // While / DoWhile take exactly one Activity body; the validator rejects a
        // second child, so the body here is always a single Sequence.
        private XElement RenderSingleBody(ActivitySpec spec, ActivitySchema schema) {
            var element = new XElement(Ns(schema) + schema.RenderName, Attributes(spec, schema));
            AddExpressionProperties(element, spec, schema);
            element.Add(new XElement(Ns(schema) + schema.RenderName + ".Body",
                WrappedBody(spec.Children, "Body")));
            return element;
        }

        private XElement RenderAssign(ActivitySpec spec, ActivitySchema schema) {
            var typed = TypedAssign(spec);

            // Without a TypeArgument the spec asks for the non-generic object
            // form, which in VB is exactly what the attribute form already is.
            if (!typed && !_settings.IsCSharp) {
                var plain = new XElement(Wf + "Assign", Attributes(spec, schema));
                AddChildren(plain, spec.Children);
                return plain;
            }

            if (!typed) {
                Warn("An Assign without \"TypeArgument\" renders the non-generic object form. Pass \"TypeArgument\": \"Int32\" (the variable's type) for the preferred Assign<T>, which surfaces type mismatches at validate time.");
            }

            var element = new XElement(Wf + "Assign", Attributes(spec, schema));
            var token = DeclaredTypeArgument(spec) ?? "x:Object";
            AddArgumentProperty(element, schema, "To", PropertyValue(spec, "To"), token, ArgumentDirection.Out);
            AddArgumentProperty(element, schema, "Value", PropertyValue(spec, "Value"), token, ArgumentDirection.In);
            return element;
        }

        // InvokeWorkflowFile and InvokeCode both bind their parameters through an
        // <Arguments> scg:Dictionary keyed by name; neither takes an activity body.
        private XElement RenderArguments(ActivitySpec spec, ActivitySchema schema) {
            var element = new XElement(Ns(schema) + schema.RenderName, Attributes(spec, schema));
            AddExpressionProperties(element, spec, schema);
            if (spec.Arguments is not { Count: > 0 }) {
                return element;
            }

            UseAlias("scg");
            var dictionary = new XElement(Scg + "Dictionary",
                new XAttribute(X + "TypeArguments", "x:String, Argument"));
            foreach (var argument in spec.Arguments) {
                dictionary.Add(RenderArgumentMapping(argument));
            }

            element.Add(new XElement(Ns(schema) + schema.RenderName + ".Arguments", dictionary));
            return element;
        }

        private XElement RenderArgumentMapping(ArgumentMappingSpec argument) {
            var direction = NormalizeDirection(argument.Direction);
            var token = TypeToken.Render(string.IsNullOrWhiteSpace(argument.Type) ? "String" : argument.Type);
            UseAliasesIn(token);

            var node = new XElement(Wf + ArgumentElement(direction),
                new XAttribute(X + "TypeArguments", token),
                new XAttribute(X + "Key", argument.Name));
            AddBinding(node, argument.Value, token, direction);
            return node;
        }

        private static ArgumentDirection NormalizeDirection(string? direction) {
            if (string.IsNullOrWhiteSpace(direction) || direction.Equals("In", StringComparison.OrdinalIgnoreCase)) {
                return ArgumentDirection.In;
            }

            return direction.Equals("Out", StringComparison.OrdinalIgnoreCase)
                ? ArgumentDirection.Out
                : ArgumentDirection.InOut;
        }

        private static string ArgumentElement(ArgumentDirection direction) => direction switch {
            ArgumentDirection.Out => "OutArgument",
            ArgumentDirection.InOut => "InOutArgument",
            _ => "InArgument"
        };

        private XElement RenderVariables(List<VariableSpec> variables) =>
            new(Wf + "Sequence.Variables", variables.Select(RenderVariable));

        private XElement RenderVariable(VariableSpec variable) {
            var token = TypeToken.Render(variable.Type);
            UseAliasesIn(token);
            var element = new XElement(Wf + "Variable",
                new XAttribute(X + "TypeArguments", token),
                new XAttribute("Name", variable.Name));
            if (variable.Default is null) {
                return element;
            }

            // A C# project cannot carry the default on the attribute: the
            // attribute parser builds a VisualBasicValue<T> from it. The typed
            // <Variable.Default> element with a CSharpValue is the C# form.
            if (_settings.IsCSharp) {
                element.Add(new XElement(Wf + "Variable.Default",
                    new XElement(Wf + "CSharpValue",
                        new XAttribute(X + "TypeArguments", token),
                        Unwrap(variable.Default))));
                return element;
            }

            element.Add(new XAttribute("Default", variable.Default));
            return element;
        }

        // ---- bodies ------------------------------------------------------------

        // Rule 24: every container body/branch slot holds a <Sequence>, even a
        // single-activity one. A body that already is one Sequence is used as-is
        // so the wrap never doubles up.
        private XElement WrappedBody(List<ActivitySpec>? children, string? displayName) {
            // A caller-supplied single Sequence owns its own variables, so it is
            // reused in place of the wrap rather than nested inside one.
            if (children is [{ } only] && IsSequence(only)) {
                return Element(only, includeVariables: only.Variables is { Count: > 0 });
            }

            var sequence = new XElement(Wf + "Sequence");
            if (displayName is not null) {
                sequence.Add(new XAttribute("DisplayName", displayName));
            }

            AddChildren(sequence, children);
            return sequence;
        }

        // The ActivityAction + DelegateInArgument body of an iteration scope.
        private XElement Body(string typeArgument, string itemName, List<ActivitySpec>? children) {
            UseAliasesIn(typeArgument);
            return new XElement(Wf + "ActivityAction",
                new XAttribute(X + "TypeArguments", typeArgument),
                new XElement(Wf + "ActivityAction.Argument",
                    new XElement(Wf + "DelegateInArgument",
                        new XAttribute(X + "TypeArguments", typeArgument),
                        new XAttribute("Name", itemName))),
                WrappedBody(children, "Body"));
        }

        private bool IsSequence(ActivitySpec spec) =>
            _catalog.TryGet(spec.Name, out var schema)
            && string.Equals(schema.Name, "Sequence", StringComparison.OrdinalIgnoreCase);

        private void AddChildren(XElement element, List<ActivitySpec>? children) {
            foreach (var child in children ?? []) {
                // A nested Sequence may declare its own variables (readability:
                // a variable is scoped to the block that uses it).
                element.Add(Element(child, includeVariables: IsSequence(child)));
            }
        }

        // ---- properties --------------------------------------------------------

        // Properties → attributes. TypeArgument properties render first as
        // x:TypeArguments (UiPath's conventional order); the rest follow schema
        // order. Spec keys match schema property names case-insensitively.
        //
        // In a C# project an expression property is skipped here whenever it gets
        // a typed property element instead (see AddExpressionProperties).
        private List<XAttribute> Attributes(ActivitySpec spec, ActivitySchema schema, string? exclude = null) {
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
                    attributes.Add(new XAttribute(X + "TypeArguments", token));
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
        private bool RendersAsArgumentElement(ActivitySpec spec, ActivitySchema schema, PropertySchema property, string value) {
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

        private void AddExpressionProperties(XElement element, ActivitySpec spec, ActivitySchema schema) {
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

        private void AddArgumentProperty(
            XElement element, ActivitySchema schema, string propertyName, string? value, string token, ArgumentDirection direction) {
            if (value is null) {
                return;
            }

            UseAliasesIn(token);
            var argumentName = ArgumentElement(direction);
            var argument = new XElement(Wf + argumentName, new XAttribute(X + "TypeArguments", token));
            AddBinding(argument, value, token, direction);
            element.Add(new XElement(Ns(schema) + schema.RenderName + "." + propertyName, argument));
        }

        // Fills an argument element: a C# project gets the typed
        // CSharpValue/CSharpReference child, a VB project keeps the [bracket]
        // text the attribute form used to carry.
        private void AddBinding(XElement argument, string? value, string token, ArgumentDirection direction) {
            if (string.IsNullOrEmpty(value)) {
                return;
            }

            if (!_settings.IsCSharp) {
                argument.Add(value);
                return;
            }

            var isWrite = direction is ArgumentDirection.Out or ArgumentDirection.InOut;
            argument.Add(new XElement(Wf + (isWrite ? "CSharpReference" : "CSharpValue"),
                new XAttribute(X + "TypeArguments", token),
                Unwrap(value)));
        }

        private ArgumentDirection ArgumentDirectionOf(ActivitySchema schema, string propertyName) =>
            ActivityCatalog.ExpressionArgument(schema.Name, propertyName)?.Direction ?? ArgumentDirection.In;

        // The x:TypeArguments token an expression property binds through. Types
        // that vary with the spec's own TypeArgument (Assign, Switch, ForEach)
        // are derived here; fixed types come from the catalog; anything else is
        // assumed x:Object and reported.
        private string ArgumentToken(ActivitySpec spec, ActivitySchema schema, string propertyName) {
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
        private static string? DeclaredTypeArgument(ActivitySpec spec) {
            var declared = PropertyValue(spec, "TypeArgument");
            return string.IsNullOrWhiteSpace(declared) ? null : TypeToken.Render(declared);
        }

        private static bool TypedAssign(ActivitySpec spec) =>
            !string.IsNullOrWhiteSpace(PropertyValue(spec, "TypeArgument"));

        // A property the schema does not know about is passed through, but never
        // silently. Two corrections apply so the passthrough cannot produce XAML
        // that looks valid but is not: an unprefixed XAML-language member
        // (typeArguments, key) is qualified as x: with its canonical casing, and a
        // property naming an xmlns prefix this builder does not declare is omitted
        // with a warning rather than emitted as a name LINQ-to-XML cannot represent.
        private XAttribute? UnknownAttribute(ActivitySchema schema, string key, string value) {
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
                return new XAttribute(X + "TypeArguments", value);
            }

            if (key.Equals("Key", StringComparison.OrdinalIgnoreCase)) {
                Warn($"Property \"{key}\" of \"{schema.Name}\" is the XAML language member x:Key, not an activity property; it was rendered as \"x:Key\". An unprefixed {key} attribute is not valid XAML.");
                return new XAttribute(X + "Key", value);
            }

            Warn($"Property \"{key}\" is not in the schema for \"{schema.Name}\"; it was passed through as an attribute. Verify the name against the activity's documentation (activities get-default-xaml).");
            return new XAttribute(key, value);
        }

        private static XNamespace? KnownPrefixNamespace(string prefix) => prefix switch {
            "x" => X,
            "sap" => Sap,
            "sap2010" => Sap2010,
            "mc" => Mc,
            "ui" => Ui,
            _ => null
        };

        private void Warn(string message) {
            if (!_warned.Add(message) || Warnings.Count >= MaxWarnings) {
                return;
            }

            Warnings.Add(message);
        }

        private static string? PropertyValue(ActivitySpec spec, string propertyName) {
            if (spec.Properties is null) {
                return null;
            }

            if (spec.Properties.TryGetValue(propertyName, out var exact)) {
                return exact;
            }

            foreach (var (key, value) in spec.Properties) {
                if (string.Equals(key, propertyName, StringComparison.OrdinalIgnoreCase)) {
                    return value;
                }
            }

            return null;
        }

        // [expr] is VB bracket shorthand; a C# binding carries the raw expression.
        private static string Unwrap(string value) => ExpressionValue.Unwrap(value);

        private static XNamespace Ns(ActivitySchema schema) => schema.XmlNamespace;

        private bool UsesNamespace(ActivitySpec spec, XNamespace ns) {
            if (_catalog.TryGet(spec.Name, out var schema) && schema.XmlNamespace == ns.NamespaceName) {
                return true;
            }

            foreach (var child in spec.Children ?? []) {
                if (UsesNamespace(child, ns)) {
                    return true;
                }
            }

            foreach (var child in spec.Else ?? []) {
                if (UsesNamespace(child, ns)) {
                    return true;
                }
            }

            foreach (var child in spec.Default ?? []) {
                if (UsesNamespace(child, ns)) {
                    return true;
                }
            }

            foreach (var switchCase in spec.Cases ?? []) {
                foreach (var child in switchCase.Children ?? []) {
                    if (UsesNamespace(child, ns)) {
                        return true;
                    }
                }
            }

            foreach (var catchSpec in spec.Catches ?? []) {
                foreach (var child in catchSpec.Children ?? []) {
                    if (UsesNamespace(child, ns)) {
                        return true;
                    }
                }
            }

            return false;
        }
    }
}
