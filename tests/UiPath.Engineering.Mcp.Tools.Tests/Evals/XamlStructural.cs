using System.Xml.Linq;
using UiPath.Engineering.Mcp.Core.Models;
using UiPath.Engineering.Mcp.Core.Parsing;

namespace UiPath.Engineering.Mcp.Tools.Tests.Evals;

/// <summary>
/// The XML namespaces and XNames the structural eval assertions address. Kept in
/// one place so the evals cannot drift from the XAML the builder emits.
/// </summary>
internal static class EvalNs {
    public static readonly XNamespace Wf = "http://schemas.microsoft.com/netfx/2009/xaml/activities";
    public static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";
    public static readonly XNamespace Ui = "http://schemas.uipath.com/workflow/activities";
    public static readonly XNamespace Uix = "http://schemas.uipath.com/workflow/activities/uix";
    public static readonly XNamespace Sap = "http://schemas.microsoft.com/netfx/2009/xaml/activities/presentation";
    public static readonly XNamespace Sap2010 = "http://schemas.microsoft.com/netfx/2010/xaml/activities/presentation";
    public static readonly XNamespace Scg = "clr-namespace:System.Collections.Generic;assembly=System.Private.CoreLib";
    public static readonly XNamespace Sd = "clr-namespace:System.Data;assembly=System.Data";
    public static readonly XNamespace Av = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    public static readonly XName IdRef = Sap2010 + "WorkflowViewState.IdRef";
    public static readonly XName HintSize = Sap + "VirtualizedContainerService.HintSize";
    public static readonly XName ViewState = Sap + "WorkflowViewStateService.ViewState";
    public static readonly XName Annotation = Sap2010 + "Annotation.AnnotationText";

    /// <summary>The body/branch property elements Rule 24 requires a &lt;Sequence&gt; inside.</summary>
    public static readonly IReadOnlySet<string> BodySlotProperties = new HashSet<string>(StringComparer.Ordinal) {
        "Then", "Else", "Try", "Default", "Body", "ActivityBody", "Entry"
    };
}

/// <summary>
/// A parsed XAML document plus a failure recorder. Every structural eval builds a
/// spec, renders it, then feeds the emitted XAML through one of the
/// <c>Require*</c> checks; any failure is collected rather than thrown so one run
/// reports every structural violation instead of only the first.
/// </summary>
/// <remarks>
/// This replaces the old <c>xaml.Contains(token)</c> substring mechanism, which
/// certified a bare <c>&lt;ui:RetryScope&gt;</c> as a correct container and an
/// unwrapped <c>If.Then</c> as a Rule 24 branch: neither difference is visible to
/// a substring scan. Every method here parses and inspects element structure.
/// </remarks>
internal sealed class Structural {
    private readonly XDocument _doc;

    private Structural(XDocument doc) => _doc = doc;

    public List<string> Failures { get; } = [];

    public XElement Root => _doc.Root!;

    public static Structural Parse(string xaml) => new(XDocument.Parse(xaml));

    public static Structural TryParse(string xaml, out string? parseError) {
        try {
            parseError = null;
            return new Structural(XDocument.Parse(xaml));
        } catch (Exception ex) {
            parseError = ex.Message;
            return new Structural(new XDocument(new XElement("Unparsed")));
        }
    }

    public Structural Require(bool condition, string message) {
        if (!condition) {
            Failures.Add(message);
        }

        return this;
    }

    // ---------------------------------------------------------------- lookups

    public XElement? Find(XNamespace ns, string localName) =>
        _doc.Descendants(ns + localName).FirstOrDefault();

    public IReadOnlyList<XElement> FindAll(XNamespace ns, string localName) =>
        [.. _doc.Descendants(ns + localName)];

    public Structural RequireElement(XNamespace ns, string localName) =>
        Require(Find(ns, localName) is not null, $"missing <{localName}> in {ns.NamespaceName}");

    public Structural RequireNoElement(XNamespace ns, string localName) =>
        Require(Find(ns, localName) is null, $"unexpected <{localName}> in {ns.NamespaceName}");

    public Structural RequireAttribute(XElement element, XName name, string? expected, string where) {
        var actual = element.Attribute(name)?.Value;
        return expected is null
            ? Require(actual is not null, $"{where}: missing attribute {name.LocalName}")
            : Require(actual == expected, $"{where}: {name.LocalName} expected \"{expected}\" but was \"{actual}\"");
    }

    // A dotted attached-property container, e.g. <ui:ForEachRow.Body>.
    public XElement? PropertyElement(XElement owner, string property) =>
        owner.Elements().FirstOrDefault(e => e.Name.LocalName == $"{owner.Name.LocalName}.{property}");

    // ----------------------------------------------------- activity identity

    /// <summary>
    /// Real activity elements: not dotted attached-property containers, not XAML
    /// infrastructure, and not ViewState dictionary values (av:Point / av:Size).
    /// </summary>
    public IReadOnlyList<XElement> Activities() =>
        [.. _doc.Root!.DescendantsAndSelf().Where(IsActivity)];

    public static bool IsActivity(XElement element) =>
        !element.Name.LocalName.Contains('.')
        && !XamlWorkflowParser.NonActivityElements.Contains(element.Name.LocalName)
        && !XamlWorkflowParser.IsWithinViewState(element);

    /// <summary>
    /// sap2010:WorkflowViewState.IdRef on every activity, and unique across the
    /// whole document. IdRef is Studio's stable activity handle and the value
    /// ValidateDiagnosticMapper matches a CLI diagnostic on.
    /// </summary>
    public Structural RequireIdRefsOnEveryActivity() {
        foreach (var activity in Activities()) {
            Require(activity.Attribute(EvalNs.IdRef)?.Value is { Length: > 0 },
                $"<{activity.Name.LocalName}> has no sap2010:WorkflowViewState.IdRef");
        }

        var duplicates = _doc.Root!.DescendantsAndSelf()
            .Select(e => e.Attribute(EvalNs.IdRef)?.Value)
            .Where(v => v is { Length: > 0 })
            .GroupBy(v => v, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();
        return Require(duplicates.Count == 0, $"duplicate IdRef(s): {string.Join(", ", duplicates)}");
    }

    /// <summary>sap:VirtualizedContainerService.HintSize on every activity, so the designer can size the node.</summary>
    public Structural RequireHintSizeOnEveryActivity() {
        foreach (var activity in Activities()) {
            Require(activity.Attribute(EvalNs.HintSize)?.Value is { Length: > 0 },
                $"<{activity.Name.LocalName}> has no sap:VirtualizedContainerService.HintSize");
        }

        return this;
    }

    /// <summary>Root &lt;Activity&gt; with x:Class, and the standard namespace declarations.</summary>
    public Structural RequireRootActivity() {
        Require(Root.Name == EvalNs.Wf + "Activity", $"root element is <{Root.Name.LocalName}>, expected <Activity>");
        Require(Root.Attribute(EvalNs.X + "Class") is not null, "root <Activity> has no x:Class");
        return this;
    }

    // --------------------------------------------------------- container shape

    /// <summary>
    /// The Rule 24 wrap on every body/branch slot in the document: each
    /// <c>.Then</c>/<c>.Else</c>/<c>.Try</c>/<c>.Default</c>/<c>.Body</c>/
    /// <c>.ActivityBody</c>/<c>.Entry</c> property element holds a
    /// <c>&lt;Sequence&gt;</c> — directly, or as the last child of the
    /// <c>ActivityAction</c> wrap a scope body uses. A bare activity in any of
    /// these slots is a Rule 24 violation the old substring harness could not see.
    /// </summary>
    public Structural RequireRule24Wraps() {
        foreach (var property in _doc.Descendants().Where(e => IsBodySlot(e))) {
            var body = BodySequence(property);
            if (body is null) {
                Failures.Add($"<{property.Name.LocalName}> is missing its Rule 24 <Sequence> body wrap");
                continue;
            }

            Require(body.Name == EvalNs.Wf + "Sequence",
                $"<{property.Name.LocalName}> body is <{body.Name.LocalName}>, expected a <Sequence> wrap");
        }

        // Switch cases: each keyed branch is a Sequence carrying x:Key.
        foreach (var switchActivity in FindAll(EvalNs.Wf, "Switch")) {
            foreach (var caseElement in switchActivity.Elements().Where(e => !e.Name.LocalName.Contains('.'))) {
                Require(caseElement.Name == EvalNs.Wf + "Sequence"
                        && caseElement.Attribute(EvalNs.X + "Key") is not null,
                    $"<Switch> case is <{caseElement.Name.LocalName}>; expected a keyed <Sequence>");
            }
        }

        // TryCatch.Catches: each Catch body is an ActivityAction whose body is a Sequence.
        foreach (var catches in FindAll(EvalNs.Wf, "TryCatch.Catches")) {
            var catchElements = catches.Elements(EvalNs.Wf + "Catch").ToList();
            Require(catchElements.Count > 0, "<TryCatch.Catches> holds no <Catch>");
            foreach (var catchElement in catchElements) {
                var action = catchElement.Element(EvalNs.Wf + "ActivityAction");
                if (action is null) {
                    Failures.Add("<Catch> has no <ActivityAction> body wrap");
                    continue;
                }

                var body = action.Elements().LastOrDefault(e => !e.Name.LocalName.Contains('.'));
                Require(body?.Name == EvalNs.Wf + "Sequence",
                    $"<Catch> body is <{body?.Name.LocalName ?? "missing"}>, expected a <Sequence> wrap");
            }
        }

        return this;
    }

    private static bool IsBodySlot(XElement element) {
        var local = element.Name.LocalName;
        var dot = local.LastIndexOf('.');
        return dot > 0
            && dot < local.Length - 1
            && EvalNs.BodySlotProperties.Contains(local[(dot + 1)..]);
    }

    // The Sequence a body/branch slot holds, unwrapping the ActivityAction a
    // scope body (RetryScope.ActivityBody, ForEachRow.Body) uses.
    private static XElement? BodySequence(XElement propertyElement) {
        var action = propertyElement.Elements().FirstOrDefault(e => e.Name == EvalNs.Wf + "ActivityAction");
        return action is not null
            ? action.Elements().LastOrDefault(e => !e.Name.LocalName.Contains('.'))
            : propertyElement.Elements().FirstOrDefault(e => !e.Name.LocalName.Contains('.'));
    }

    /// <summary>
    /// A property element holding an untyped <c>ActivityAction</c> with no
    /// DelegateInArgument — e.g. <c>RetryScope.ActivityBody</c>.
    /// </summary>
    public Structural RequireUntypedActionBody(XElement owner, string property) {
        var slot = PropertyElement(owner, property);
        if (slot is null) {
            Failures.Add($"<{owner.Name.LocalName}> has no <. {property}> body property element");
            return this;
        }

        var action = slot.Element(EvalNs.Wf + "ActivityAction");
        if (action is null) {
            Failures.Add($"<{owner.Name.LocalName}.{property}> does not hold an <ActivityAction>");
            return this;
        }

        Require(action.Attribute(EvalNs.X + "TypeArguments") is null,
            $"<{owner.Name.LocalName}.{property}> ActivityAction must be untyped but has x:TypeArguments");
        Require(action.Element(EvalNs.Wf + "ActivityAction.Argument") is null,
            $"<{owner.Name.LocalName}.{property}> untyped ActivityAction must have no DelegateInArgument");
        var body = action.Elements().LastOrDefault(e => !e.Name.LocalName.Contains('.'));
        return Require(body?.Name == EvalNs.Wf + "Sequence",
            $"<{owner.Name.LocalName}.{property}> ActivityAction body must be a <Sequence>");
    }

    /// <summary>
    /// A property element holding <c>ActivityAction&lt;T&gt;</c> plus a
    /// <c>DelegateInArgument</c> of the same type — e.g. <c>ForEachRow.Body</c>.
    /// </summary>
    public Structural RequireTypedActionBody(XElement owner, string property, string typeArgument, string delegateName) {
        var slot = PropertyElement(owner, property);
        if (slot is null) {
            Failures.Add($"<{owner.Name.LocalName}> has no <. {property}> body property element");
            return this;
        }

        var action = slot.Element(EvalNs.Wf + "ActivityAction");
        if (action is null) {
            Failures.Add($"<{owner.Name.LocalName}.{property}> does not hold an <ActivityAction>");
            return this;
        }

        Require(action.Attribute(EvalNs.X + "TypeArguments")?.Value == typeArgument,
            $"<{owner.Name.LocalName}.{property}> ActivityAction x:TypeArguments expected \"{typeArgument}\" but was \"{action.Attribute(EvalNs.X + "TypeArguments")?.Value}\"");
        var argument = action.Element(EvalNs.Wf + "ActivityAction.Argument")?.Element(EvalNs.Wf + "DelegateInArgument");
        if (argument is null) {
            Failures.Add($"<{owner.Name.LocalName}.{property}> has no ActivityAction.Argument/DelegateInArgument");
            return this;
        }

        Require(argument.Attribute(EvalNs.X + "TypeArguments")?.Value == typeArgument,
            $"DelegateInArgument x:TypeArguments expected \"{typeArgument}\" but was \"{argument.Attribute(EvalNs.X + "TypeArguments")?.Value}\"");
        Require(argument.Attribute("Name")?.Value == delegateName,
            $"DelegateInArgument Name expected \"{delegateName}\" but was \"{argument.Attribute("Name")?.Value}\"");
        var body = action.Elements().LastOrDefault(e => !e.Name.LocalName.Contains('.'));
        return Require(body?.Name == EvalNs.Wf + "Sequence",
            $"<{owner.Name.LocalName}.{property}> ActivityAction body must be a <Sequence>");
    }

    /// <summary>A branch slot that holds a bare <c>&lt;Sequence&gt;</c>: If.Then / If.Else / Switch.Default.</summary>
    public Structural RequireSequenceSlot(XElement owner, string property) {
        var slot = PropertyElement(owner, property);
        if (slot is null) {
            Failures.Add($"<{owner.Name.LocalName}> has no <. {property}> property element");
            return this;
        }

        var body = slot.Elements().FirstOrDefault(e => !e.Name.LocalName.Contains('.'));
        return Require(body?.Name == EvalNs.Wf + "Sequence",
            $"<{owner.Name.LocalName}.{property}> expected a <Sequence> wrap but found <{body?.Name.LocalName ?? "nothing"}>");
    }

    /// <summary>The Assign generic form: x:TypeArguments plus a typed To/Value argument pair.</summary>
    public Structural RequireTypedAssign(string typeArgument) {
        var assign = Find(EvalNs.Wf, "Assign");
        if (assign is null) {
            Failures.Add("missing <Assign>");
            return this;
        }

        Require(assign.Attribute(EvalNs.X + "TypeArguments")?.Value == typeArgument,
            $"<Assign> x:TypeArguments expected \"{typeArgument}\" but was \"{assign.Attribute(EvalNs.X + "TypeArguments")?.Value}\"");

        var to = PropertyElement(assign, "To")?.Elements().FirstOrDefault();
        var value = PropertyElement(assign, "Value")?.Elements().FirstOrDefault();
        Require(to?.Name == EvalNs.Wf + "OutArgument" && to.Attribute(EvalNs.X + "TypeArguments")?.Value == typeArgument,
            "<Assign.To> must be an OutArgument with the Assign's x:TypeArguments");
        Require(value?.Name == EvalNs.Wf + "InArgument" && value.Attribute(EvalNs.X + "TypeArguments")?.Value == typeArgument,
            "<Assign.Value> must be an InArgument with the Assign's x:TypeArguments");
        return this;
    }

    // ----------------------------------------------------------- annotations

    /// <summary>
    /// sap2010:Annotation.AnnotationText round-trip: the attribute carries the
    /// spec text, and XamlWorkflowParser surfaces it as ActivityModel.Annotation.
    /// </summary>
    public Structural RequireAnnotationRoundTrip(XElement element, string expected) {
        Require(element.Attribute(EvalNs.Annotation)?.Value == expected,
            $"annotation attribute expected \"{expected}\" but was \"{element.Attribute(EvalNs.Annotation)?.Value}\"");
        var parsed = new XamlWorkflowParser().Parse("eval.xaml", "eval.xaml", _doc.Root!.ToString());
        var model = parsed.Activities.FirstOrDefault(a =>
            string.Equals(a.DisplayName, element.Attribute("DisplayName")?.Value ?? a.DisplayName, StringComparison.Ordinal)
            && a.Type == element.Name.LocalName);
        return Require(model?.Annotation == expected,
            $"annotation did not round-trip into ActivityModel.Annotation (got \"{model?.Annotation}\")");
    }

    // -------------------------------------------------------- diagram graphs

    /// <summary>
    /// Rule 20 structure-first for Flowchart / StateMachine: every node is a
    /// direct child, every node carries ShapeLocation + ShapeSize, every link
    /// resolves to a declared node, and no node is an orphan.
    /// </summary>
    public Structural RequireDiagram(XNamespace diagramNs, string diagramLocalName, params string[] nodeLocalNames) {
        var diagram = Find(diagramNs, diagramLocalName);
        if (diagram is null) {
            Failures.Add($"missing <{diagramLocalName}>");
            return this;
        }

        var nodeNames = nodeLocalNames.ToHashSet(StringComparer.Ordinal);
        var nodes = diagram.Elements().Where(e => nodeNames.Contains(e.Name.LocalName)).ToList();
        Require(nodes.Count > 0, $"<{diagramLocalName}> declares no nodes");

        var declared = new List<string>();
        foreach (var node in nodes) {
            Require(node.Parent == diagram, $"<{node.Name.LocalName}> must be a direct child of <{diagramLocalName}>");
            var name = node.Attribute(EvalNs.X + "Name")?.Value;
            Require(name is { Length: > 0 }, $"<{node.Name.LocalName}> has no x:Name");
            if (name is { Length: > 0 }) {
                declared.Add(name);
            }

            RequireHasShape(node);
        }

        // Every reference must resolve to a declared node; every declared node must
        // be reachable from the start or another node's link.
        var referenced = new HashSet<string>(
            diagram.DescendantsAndSelf().Where(e => e.Name.LocalName == "Reference").Select(e => e.Value.Trim()),
            StringComparer.Ordinal);
        var initial = InitialTarget(diagram);
        if (initial is not null) {
            referenced.Add(initial);
        }

        foreach (var target in referenced) {
            Require(declared.Contains(target), $"<{diagramLocalName}> references undeclared node \"{target}\"");
        }

        foreach (var name in declared) {
            Require(referenced.Contains(name), $"<{diagramLocalName}> node \"{name}\" is an orphan (nothing links to it)");
        }

        return this;
    }

    private void RequireHasShape(XElement node) {
        var viewState = node.Element(EvalNs.ViewState);
        if (viewState is null) {
            Failures.Add($"<{node.Name.LocalName}> has no ViewState (ShapeLocation/ShapeSize)");
            return;
        }

        var keys = viewState.Descendants()
            .Select(e => e.Attribute(EvalNs.X + "Key")?.Value)
            .Where(k => k is not null)
            .ToHashSet(StringComparer.Ordinal);
        Require(keys.Contains("ShapeLocation"), $"<{node.Name.LocalName}> has no ShapeLocation");
        Require(keys.Contains("ShapeSize"), $"<{node.Name.LocalName}> has no ShapeSize");
    }

    private static string? InitialTarget(XElement diagram) {
        if (diagram.Name.LocalName == "StateMachine") {
            var value = diagram.Attribute("InitialState")?.Value?.Trim();
            return value is null ? null : value.Replace("{x:Reference", "").TrimEnd('}').Trim();
        }

        return diagram.Element(EvalNs.Wf + $"{diagram.Name.LocalName}.StartNode")
            ?.Elements().FirstOrDefault(e => e.Name.LocalName == "Reference")?.Value.Trim();
    }

    // ------------------------------------------------------- expression form

    /// <summary>
    /// A C# project binds a non-literal expression through a typed
    /// <c>CSharpValue</c> (read) / <c>CSharpReference</c> (write) property element;
    /// a VB project keeps the <c>[bracket]</c> attribute / text form. [bracket]
    /// under C# deserializes as VisualBasicValue and breaks C#-only syntax.
    /// </summary>
    /// <param name="expectValue">
    /// Require at least one <c>&lt;CSharpValue&gt;</c>. Set false only for a spec
    /// whose C# rendering has no read binding at all.
    /// </param>
    /// <param name="expectReference">
    /// Require at least one <c>&lt;CSharpReference&gt;</c>. Set true only when the
    /// spec actually contains a write binding (an Assign.To, an Out/InOut argument).
    /// </param>
    public Structural RequireExpressionForm(bool csharp, bool expectValue = true, bool expectReference = false) {
        if (csharp) {
            Require(!expectValue || FindAll(EvalNs.Wf, "CSharpValue").Count > 0,
                "C# project emitted no <CSharpValue> property element");
            Require(!expectReference || FindAll(EvalNs.Wf, "CSharpReference").Count > 0,
                "C# project emitted no <CSharpReference> property element");
            Require(Find(EvalNs.Wf, "TextExpression.NamespacesForImplementation") is not null,
                "C# project emitted no <TextExpression.NamespacesForImplementation> import block");
            Require(Find(EvalNs.Wf, "VisualBasic.Settings") is null,
                "C# project emitted a <VisualBasic.Settings> block");
            Require(!_doc.Root!.ToString().Contains("[", StringComparison.Ordinal),
                "C# project emitted VB [bracket] shorthand");
            return this;
        }

        Require(Find(EvalNs.Wf, "VisualBasic.Settings") is not null, "VB project emitted no <VisualBasic.Settings> block");
        Require(Find(EvalNs.Wf, "CSharpValue") is null, "VB project emitted a <CSharpValue>");
        Require(Find(EvalNs.Wf, "CSharpReference") is null, "VB project emitted a <CSharpReference>");
        return this;
    }

    /// <summary>
    /// The typed argument property element for an expression property, e.g.
    /// <c>&lt;ui:LogMessage.Message&gt;&lt;InArgument&gt;&lt;CSharpValue&gt;</c>.
    /// Returns the leaf value element (<c>CSharpValue</c>/<c>CSharpReference</c>).
    /// </summary>
    public XElement? CSharpBinding(XElement owner, string property, string leaf) =>
        PropertyElement(owner, property)
            ?.Element(EvalNs.Wf + "InArgument")
            ?.Element(EvalNs.Wf + leaf);

    /// <summary>InvokeWorkflowFile / InvokeCode &lt;Arguments&gt; scg:Dictionary keyed by argument name.</summary>
    public Structural RequireArgumentDictionary(XElement owner, params (string Key, string Element, string Type)[] expected) {
        var dict = PropertyElement(owner, "Arguments")?.Element(EvalNs.Scg + "Dictionary");
        if (dict is null) {
            Failures.Add($"<{owner.Name.LocalName}> has no .Arguments scg:Dictionary");
            return this;
        }

        Require(dict.Attribute(EvalNs.X + "TypeArguments")?.Value == "x:String, Argument",
            $"<{owner.Name.LocalName}.Arguments> x:TypeArguments expected \"x:String, Argument\" but was \"{dict.Attribute(EvalNs.X + "TypeArguments")?.Value}\"");
        foreach (var (key, element, type) in expected) {
            var argument = dict.Elements().FirstOrDefault(e => e.Attribute(EvalNs.X + "Key")?.Value == key);
            if (argument is null) {
                Failures.Add($"<{owner.Name.LocalName}.Arguments> has no x:Key=\"{key}\"");
                continue;
            }

            Require(argument.Name == EvalNs.Wf + element,
                $"argument \"{key}\" expected <{element}> but was <{argument.Name.LocalName}>");
            Require(argument.Attribute(EvalNs.X + "TypeArguments")?.Value == type,
                $"argument \"{key}\" x:TypeArguments expected \"{type}\" but was \"{argument.Attribute(EvalNs.X + "TypeArguments")?.Value}\"");
        }

        return this;
    }

    // ----------------------------------------------------------- readability

    public static ActivityModel Activity(string type, string displayName, int depth = 1, string id = "") => new() {
        Id = string.IsNullOrEmpty(id) ? $"{type.ToLowerInvariant()}.{depth}" : id,
        Type = type,
        DisplayName = displayName,
        Depth = depth
    };
}