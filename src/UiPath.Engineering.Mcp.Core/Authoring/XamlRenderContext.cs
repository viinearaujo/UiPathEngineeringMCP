using System.Xml.Linq;
using UiPath.Engineering.Mcp.Core.Models;

namespace UiPath.Engineering.Mcp.Core.Authoring;

/// <summary>
/// Stateful XAML render pass: catalog, project settings, warnings, and xmlns aliases.
/// Body-shape rendering is split across partials and surfaced via the public
/// renderer types (<see cref="XamlContainerRenderer"/>, <see cref="XamlDiagramRenderer"/>,
/// <see cref="XamlMembersRenderer"/>, <see cref="XamlAttributeRenderer"/>,
/// <see cref="XamlViewStateRenderer"/>).
/// </summary>
public sealed partial class XamlRenderContext {
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

    public XamlRenderContext(IActivityCatalog catalog, ProjectXamlSettings? settings) {
        _catalog = catalog;
        _settings = settings ?? ProjectXamlSettings.Default;
        var coreAssembly = _settings.CoreAssembly;
        S = XamlNamespaces.System(coreAssembly);
        Sc = XamlNamespaces.Collections(coreAssembly);
        Scg = XamlNamespaces.Generic(coreAssembly);
        Sco = XamlNamespaces.ObjectModel(coreAssembly);
        Sd = XamlNamespaces.SystemData;
    }

    internal XNamespace S { get; }
    internal XNamespace Sc { get; }
    internal XNamespace Scg { get; }
    internal XNamespace Sco { get; }
    internal XNamespace Sd { get; }

    public List<string> Warnings { get; } = [];

    internal List<ToolError> Validate(ActivitySpec spec) => SpecValidator.Validate(spec, _catalog, _settings);

    internal XElement? Members(ActivitySpec spec) {
        if (spec.WorkflowArguments is not { Count: > 0 }) {
            return null;
        }

        var members = new XElement(XamlNamespaces.X + "Members");
        foreach (var argument in spec.WorkflowArguments) {
            var token = TypeToken.Render(string.IsNullOrWhiteSpace(argument.Type) ? "String" : argument.Type);
            UseAliasesIn(token);
            members.Add(new XElement(XamlNamespaces.X + "Property",
                new XAttribute("Name", argument.Name),
                new XAttribute("Type", $"{ArgumentWrapper(argument.Direction)}({token})")));
        }

        return members;
    }

    internal static string ArgumentWrapper(string? direction) =>
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

            return new XElement(XamlNamespaces.Wf + "VisualBasic.Settings", new XElement(XamlNamespaces.X + "Null"));
        }

        var imports = spec.Imports is { Count: > 0 } ? spec.Imports : (IReadOnlyList<string>)DefaultCSharpImports;
        var collection = new XElement(Sco + "Collection", new XAttribute(XamlNamespaces.X + "TypeArguments", "x:String"));
        foreach (var import in imports) {
            if (!string.IsNullOrWhiteSpace(import)) {
                collection.Add(new XElement(XamlNamespaces.X + "String", import.Trim()));
            }
        }

        UseAlias("sco");
        return new XElement(XamlNamespaces.Wf + "TextExpression.NamespacesForImplementation", collection);
    }

    // A fragment has no <Activity> root to carry declarations, so its root
    // element declares the namespaces it needs (appended after the property
    // attributes).
    internal void DeclareFragmentNamespaces(XElement root, ActivitySpec spec) {
        root.Add(new XAttribute("xmlns", XamlNamespaces.Wf.NamespaceName));
        root.Add(new XAttribute(XNamespace.Xmlns + "x", XamlNamespaces.X.NamespaceName));
        if (UsesAnnotation(spec)) {
            root.Add(new XAttribute(XNamespace.Xmlns + "sap2010", XamlNamespaces.Sap2010.NamespaceName));
        }

        DeclareNamespaces(root, spec);
    }

    internal static bool UsesAnnotation(ActivitySpec spec) {
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
        if (UsesNamespace(spec, XamlNamespaces.Ui) && root.Attribute(XNamespace.Xmlns + "ui") is null) {
            root.Add(new XAttribute(XNamespace.Xmlns + "ui", XamlNamespaces.Ui.NamespaceName));
        }

        if (UsesNamespace(spec, XamlNamespaces.Uix) && root.Attribute(XNamespace.Xmlns + "uix") is null) {
            root.Add(new XAttribute(XNamespace.Xmlns + "uix", XamlNamespaces.Uix.NamespaceName));
        }

        if (UsesNamespace(spec, XamlNamespaces.ModernExcel) && root.Attribute(XNamespace.Xmlns + "ueab") is null) {
            root.Add(new XAttribute(XNamespace.Xmlns + "ueab", XamlNamespaces.ModernExcel.NamespaceName));
        }
    }

    // Type tokens are rendered while children are built, but a namespace
    // declaration must appear on the outermost element that uses it. This
    // pass mirrors the token/alias rules over the same filter so the
    // declared set matches the set actually emitted.
    internal void CollectRequiredAliases(ActivitySpec spec, HashSet<string> aliases) {
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

    internal XNamespace? AliasNamespace(string alias) => alias switch {
        "s" => S,
        "sc" => Sc,
        "scg" => Scg,
        "sco" => Sco,
        "sd" => Sd,
        "ue" => XamlNamespaces.ModernExcelModel,
        "ui" => XamlNamespaces.Ui,
        "uix" => XamlNamespaces.Uix,
        "ueab" => XamlNamespaces.ModernExcel,
        _ => null
    };

    internal void UseAlias(string alias) => _aliases.Add(alias);

    internal void UseAliasesIn(string? typeArguments) {
        foreach (var alias in TypeToken.AliasesIn(typeArguments)) {
            if (AliasNamespace(alias) is not null) {
                UseAlias(alias);
            }
        }
    }

    internal XElement Element(ActivitySpec spec, bool includeVariables) {
        if (!_catalog.TryGet(spec.Name, out var schema)) {
            throw new InvalidOperationException($"Unknown activity \"{spec.Name}\".");
        }

        var element = schema.Name switch {
            "If" => RenderIf(spec, schema),
            "Switch" => RenderSwitch(spec, schema),
            "TryCatch" => RenderTryCatch(spec, schema),
            "Assign" => RenderAssign(spec, schema),
            "Flowchart" when spec.Flowchart is not null => RenderFlowchart(spec, schema),
            "StateMachine" when spec.StateMachine is not null => RenderStateMachine(spec, schema),
            _ when ActivityCatalog.AcceptsArgumentDictionary(schema.Name) => RenderArguments(spec, schema),
            _ => RenderByBodyShape(spec, schema),
        };

        if (includeVariables && spec.Variables is { Count: > 0 }) {
            element.AddFirst(RenderVariables(spec.Variables));
        }

        AddAnnotation(element, spec);
        return element;
    }

    private static void AddAnnotation(XElement element, ActivitySpec spec) {
        if (string.IsNullOrWhiteSpace(spec.Annotation)) {
            return;
        }

        element.Add(new XAttribute(XamlNamespaces.Sap2010 + "Annotation.AnnotationText", spec.Annotation));
    }

    internal void Warn(string message) {
        if (!_warned.Add(message) || Warnings.Count >= MaxWarnings) {
            return;
        }

        Warnings.Add(message);
    }

    internal static string? PropertyValue(ActivitySpec spec, string propertyName) {
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
    internal static string Unwrap(string value) => ExpressionValue.Unwrap(value);

    internal static XNamespace Ns(ActivitySchema schema) => schema.XmlNamespace;

    internal bool UsesNamespace(ActivitySpec spec, XNamespace ns) {
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
