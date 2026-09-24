using System.Xml.Linq;
using UiPath.Engineering.Mcp.Core.Parsing;

namespace UiPath.Engineering.Mcp.Core.Authoring;

/// <summary>
/// Assigns the designer state Studio writes for every activity: a per-type
/// sequential <c>sap2010:WorkflowViewState.IdRef</c>, a
/// <c>sap:VirtualizedContainerService.HintSize</c>, and — for Sequences — an
/// <c>IsExpanded</c> <c>sap:WorkflowViewStateService.ViewState</c> dictionary.
/// </summary>
/// <remarks>
/// Two reasons this exists. Studio uses IdRef as its stable activity handle, and
/// <see cref="ValidateDiagnosticMapper"/> matches CLI diagnostics on it — without
/// it diagnostics fall back to DisplayName/line matching. And <c>HintSize</c> plus
/// ViewState is what the designer reads to size a node; a file without them still
/// opens, but Studio re-lays-out the whole file on save.
///
/// The pass is additive: an element that already carries an IdRef keeps it, and
/// per-type counters are seeded from the IdRefs already in the document so an
/// inserted activity continues the sequence instead of colliding.
/// </remarks>
public static class XamlViewStateEmitter {
    internal static readonly XNamespace Sap = "http://schemas.microsoft.com/netfx/2009/xaml/activities/presentation";
    internal static readonly XNamespace Sap2010 = "http://schemas.microsoft.com/netfx/2010/xaml/activities/presentation";
    internal static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";
    internal static readonly string ScgNamespacePrefix = "clr-namespace:System.Collections.Generic;assembly=";

    /// <summary>
    /// Runs the pass over a whole document in document order: every activity
    /// without an IdRef gets one, and every activity without a HintSize gets one.
    /// </summary>
    public static void Apply(XDocument doc, string? coreAssembly = null) {
        if (doc.Root is null) {
            return;
        }

        var core = coreAssembly ?? ExistingCoreAssembly(doc.Root) ?? ProjectXamlSettings.ModernCoreAssembly;
        var counters = SeedCounters(doc);
        Apply(doc.Root, counters, core);
    }

    /// <summary>
    /// Runs the pass over freshly rendered nodes only, seeding counters from the
    /// document they are about to be inserted into so untouched regions of that
    /// document stay byte-identical.
    /// </summary>
    public static void ApplyToFragment(IEnumerable<XElement> nodes, XDocument target, string? coreAssembly = null) {
        var core = coreAssembly
            ?? (target.Root is null ? null : ExistingCoreAssembly(target.Root))
            ?? ProjectXamlSettings.ModernCoreAssembly;
        var counters = SeedCounters(target);
        foreach (var node in nodes) {
            Apply(node, counters, core);
            // The inserted element re-declares the ViewState prefixes it uses
            // (UiPath style: namespaces at point of use), so the target document
            // root is not modified.
            DeclarePrefixes(node, core);
        }
    }

    /// <summary>
    /// Declares sap:/sap2010:/scg: on an element that carries ViewState state.
    /// UiPath declares namespaces at point of use, so an insert does not rewrite
    /// the target document's root element.
    /// </summary>
    public static void DeclarePrefixes(XElement element, string coreAssembly) {
        element.SetAttributeValue(XNamespace.Xmlns + "sap", Sap.NamespaceName);
        element.SetAttributeValue(XNamespace.Xmlns + "sap2010", Sap2010.NamespaceName);
        if (element.Attribute(XNamespace.Xmlns + "scg") is null) {
            element.SetAttributeValue(XNamespace.Xmlns + "scg", ScgNamespacePrefix + coreAssembly);
        }
    }

    private static void Apply(XElement root, Dictionary<string, int> counters, string coreAssembly) {
        foreach (var element in root.DescendantsAndSelf()) {
            if (!IsActivity(element)) {
                continue;
            }

            var name = element.Name.LocalName;
            if (element.Attribute(Sap2010 + "WorkflowViewState.IdRef") is null) {
                counters.TryGetValue(name, out var next);
                next++;
                counters[name] = next;
                element.SetAttributeValue(Sap2010 + "WorkflowViewState.IdRef", $"{name}_{next}");
            }

            if (element.Attribute(Sap + "VirtualizedContainerService.HintSize") is null) {
                element.SetAttributeValue(Sap + "VirtualizedContainerService.HintSize", HintSizeFor(name));
            }

            if (IsSequence(name) && element.Element(Sap + "WorkflowViewStateService.ViewState") is null) {
                element.AddFirst(RenderExpandedViewState(coreAssembly));
            }
        }
    }

    // A Sequence shows as expanded so its children are visible on the canvas.
    private static XElement RenderExpandedViewState(string coreAssembly) =>
        new(Sap + "WorkflowViewStateService.ViewState",
            new XElement(XNamespace.Get($"clr-namespace:System.Collections.Generic;assembly={coreAssembly}") + "Dictionary",
                new XAttribute(XName.Get("TypeArguments", "http://schemas.microsoft.com/winfx/2006/xaml"), "x:String, x:Object"),
                new XElement(X + "Boolean",
                    new XAttribute(X + "Key", "IsExpanded"),
                    "True")));

    // The assembly a document already uses for its clr-namespace: aliases, so a
    // Legacy (mscorlib) workflow is not handed a modern declaration.
    internal static string? ExistingCoreAssembly(XElement? root) {
        if (root is null) {
            return null;
        }

        foreach (var attribute in root.Attributes()) {
            if (!attribute.IsNamespaceDeclaration
                || !attribute.Value.StartsWith("clr-namespace:System;", StringComparison.Ordinal)) {
                continue;
            }

            var index = attribute.Value.IndexOf("assembly=", StringComparison.Ordinal);
            if (index >= 0) {
                return attribute.Value[(index + "assembly=".Length)..];
            }
        }

        return null;
    }

    /// <summary>
    /// The per-type IdRef counters already present in a document: the next IdRef
    /// for a type continues from the highest existing <c>&lt;Type&gt;_&lt;n&gt;</c>.
    /// </summary>
    internal static Dictionary<string, int> SeedCounters(XDocument doc) {
        var counters = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (doc.Root is null) {
            return counters;
        }

        foreach (var element in doc.Root.DescendantsAndSelf()) {
            if (element.Attribute(Sap2010 + "WorkflowViewState.IdRef")?.Value is not { Length: > 0 } idRef) {
                continue;
            }

            Seed(counters, element.Name.LocalName, idRef);
        }

        return counters;
    }

    private static void Seed(Dictionary<string, int> counters, string typeName, string idRef) {
        var underscore = idRef.LastIndexOf('_');
        if (underscore < 0 || underscore == idRef.Length - 1) {
            return;
        }

        if (int.TryParse(idRef[(underscore + 1)..], out var ordinal)) {
            counters[typeName] = Math.Max(counters.TryGetValue(typeName, out var current) ? current : 0, ordinal);
        }
    }

    /// <summary>Studio's default designer size for a node of this type.</summary>
    internal static string HintSizeFor(string activityType) => activityType switch {
        "Sequence" => "130,60",
        "If" => "258,90",
        "Switch" => "258,90",
        "TryCatch" => "258,110",
        "ForEach" or "ForEachRow" or "ExcelForEachRowX" => "200,110",
        "While" or "DoWhile" or "InterruptibleWhile" or "InterruptibleDoWhile" => "200,90",
        "RetryScope" => "258,110",
        "NApplicationCard" => "458,420",
        _ => "110,70"
    };

    private static bool IsSequence(string localName) =>
        localName.Equals("Sequence", StringComparison.OrdinalIgnoreCase);

    private static bool IsActivity(XElement element) {
        var local = element.Name.LocalName;
        return !local.Contains('.')
            && !XamlWorkflowParser.NonActivityElements.Contains(local)
            && !XamlWorkflowParser.IsWithinViewState(element);
    }
}