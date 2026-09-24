using System.Diagnostics.CodeAnalysis;

namespace UiPath.Engineering.Mcp.Core.Authoring;

public static class ActivityCatalog {
    internal static readonly (string Prefix, string Ns) Wf = ("", "http://schemas.microsoft.com/netfx/2009/xaml/activities");
    internal static readonly (string Prefix, string Ns) Ui = ("ui", "http://schemas.uipath.com/workflow/activities");
    internal static readonly (string Prefix, string Ns) Uix = ("uix", "http://schemas.uipath.com/workflow/activities/uix");
    internal static readonly (string Prefix, string Ns) ModernExcel =
        ("ueab", "clr-namespace:UiPath.Excel.Activities.Business;assembly=UiPath.Excel.Activities");

    public const string SystemPackage = "UiPath.System.Activities";
    public const string ExcelPackage = "UiPath.Excel.Activities";
    public const string UiaPackage = "UiPath.UIAutomation.Activities";

    // ---- leaf-activity factories (no body) -----------------------------------
    //
    // A framework WF activity: no NuGet package supplies it, it lives in the
    // default activities namespace, and its CLR type is System.Activities.Statements.
    private static ActivitySchema Leaf(string name, params PropertySchema[] props) =>
        new(name, Wf.Prefix, Wf.Ns, false, props, FullTypeName: $"System.Activities.Statements.{name}");

    // A UiPath.Core.Activities leaf supplied by UiPath.System.Activities.
    private static ActivitySchema UiLeaf(string name, params PropertySchema[] props) =>
        new(name, Ui.Prefix, Ui.Ns, false, props,
            PackageId: SystemPackage, FullTypeName: $"UiPath.Core.Activities.{name}");

    // A UiPath.Core.Activities leaf supplied by a package other than
    // UiPath.System.Activities (the classic Excel family).
    private static ActivitySchema UiLeafPackaged(string packageId, string fullTypeName, string name, params PropertySchema[] props) =>
        new(name, Ui.Prefix, Ui.Ns, false, props, PackageId: packageId, FullTypeName: fullTypeName);
    // A modern Excel "X" leaf: a clr-namespace alias, no scope body of its own.
    private static ActivitySchema ModernExcelLeaf(string name, params PropertySchema[] props) =>
        new(name, ModernExcel.Prefix, ModernExcel.Ns, false, props,
            PackageId: ExcelPackage, FullTypeName: $"UiPath.Excel.Activities.Business.{name}");

    // ---- container-activity factories ----------------------------------------

    private static ActivitySchema Container(string name, BodyDescriptor? body, params PropertySchema[] props) =>
        new(name, Wf.Prefix, Wf.Ns, true, props,
            FullTypeName: $"System.Activities.Statements.{name}", Body: body);

    // A UiPath.Core.Activities container. <paramref name="element"/> is the emitted
    // element when the toolbox label differs from the type (the "While" item emits
    // InterruptibleWhile).
    private static ActivitySchema UiContainer(
        string name, BodyDescriptor body, string? element = null, string? fullType = null, params PropertySchema[] props) =>
        new(name, Ui.Prefix, Ui.Ns, true, props,
            PackageId: SystemPackage,
            FullTypeName: fullType ?? $"UiPath.Core.Activities.{element ?? name}",
            Body: body, ElementName: element);

    private static ActivitySchema UiContainerPackaged(
        string packageId, string fullTypeName, string name, BodyDescriptor body, params PropertySchema[] props) =>
        new(name, Ui.Prefix, Ui.Ns, true, props, PackageId: packageId, FullTypeName: fullTypeName, Body: body);

    private static ActivitySchema ModernExcelContainer(string name, BodyDescriptor body, params PropertySchema[] props) =>
        new(name, ModernExcel.Prefix, ModernExcel.Ns, true, props,
            PackageId: ExcelPackage, FullTypeName: $"UiPath.Excel.Activities.Business.{name}", Body: body);

    private static ActivitySchema UiaLeaf(string name, params PropertySchema[] props) =>
        new(name, Uix.Prefix, Uix.Ns, false, props,
            PackageId: UiaPackage, FullTypeName: $"UiPath.UIAutomationNext.Activities.{name}");

    private static ActivitySchema UiaContainer(string name, BodyDescriptor body, params PropertySchema[] props) =>
        new(name, Uix.Prefix, Uix.Ns, true, props,
            PackageId: UiaPackage, FullTypeName: $"UiPath.UIAutomationNext.Activities.{name}", Body: body);

    // ---- property helpers ----------------------------------------------------

    private static PropertySchema E(string name, bool required = true) => new(name, required, PropertyKind.Expression);
    private static PropertySchema L(string name, bool required = false) => new(name, required, PropertyKind.Literal);
    private static PropertySchema T(string name, bool required = true) => new(name, required, PropertyKind.TypeArgument);

    private static PropertySchema Enum(string name, params string[] allowed) =>
        new(name, false, PropertyKind.Literal, AllowedValues: allowed);

    public static IReadOnlyList<ActivitySchema> All { get; } =
    [
        // ---- framework control flow ----
        // Sequence's content property takes its children directly (no property
        // element), so it has no body descriptor: the generic renderer emits them.
        Container("Sequence", body: null, L("DisplayName")),
        // TypeArgument selects the preferred generic Assign<T> form; omitting it
        // renders the non-generic object/object Assign.
        new ActivitySchema("Assign", Wf.Prefix, Wf.Ns, false,
            [L("DisplayName"), T("TypeArgument", required: false), E("To"), E("Value")],
            FullTypeName: "System.Activities.Statements.Assign`1"),
        Container("If", new BodyDescriptor(BodyShape.Branches, "Then"),
            L("DisplayName"), E("Condition")),
        Container("Switch", new BodyDescriptor(BodyShape.Branches, "Cases"),
            L("DisplayName"), E("Expression"), T("TypeArgument")),
        Container("TryCatch", new BodyDescriptor(BodyShape.Branches, "Try"),
            L("DisplayName")),
        // Flowchart and StateMachine are graph containers: their nodes live in the
        // spec's flowchart/stateMachine object and are wired by x:Reference, not
        // nested as children (Rule 20 structure-first).
        Container("Flowchart", new BodyDescriptor(BodyShape.ActivityCollection, "Nodes"),
            L("DisplayName")),
        Container("StateMachine", new BodyDescriptor(BodyShape.ActivityCollection, "States"),
            L("DisplayName"), L("InitialState")),
        Container("FlowStep", new BodyDescriptor(BodyShape.Activity, "Activity"),
            L("DisplayName")),
        Container("State", new BodyDescriptor(BodyShape.ActivityCollection, "Entry"),
            L("DisplayName")),
        Leaf("FlowDecision", L("DisplayName"), E("Condition")),
        Leaf("FlowSwitch", L("DisplayName"), E("Expression"), T("TypeArgument")),
        Leaf("Transition", L("DisplayName"), E("Condition")),
        Leaf("FinalState", L("DisplayName")),
        Leaf("WriteLine", L("DisplayName"), E("Text")),
        Leaf("Delay", L("DisplayName"), E("Duration")),
        Leaf("Throw", L("DisplayName"), E("Exception")),
        Leaf("Rethrow", L("DisplayName")),

        // ---- UiPath loop wraps (Studio emits these, not the framework types) ----
        UiContainer("While", new BodyDescriptor(BodyShape.Activity, "Body"), element: "InterruptibleWhile",
            props: [L("DisplayName"), E("Condition"), L("MaxIterations")]),
        UiContainer("DoWhile", new BodyDescriptor(BodyShape.Activity, "Body"), element: "InterruptibleDoWhile",
            props: [L("DisplayName"), E("Condition"), L("MaxIterations")]),
        UiContainer("ForEach", new BodyDescriptor(BodyShape.TypedAction, "Body", "System.Collections.IEnumerable", "item"),
            element: "ForEach", fullType: "UiPath.Core.Activities.ForEach`1",
            props: [L("DisplayName"), E("Values"), T("TypeArgument"), L("ItemName"), L("MaxIterations")]),

        // ---- UiPath System package data + logging ----
        UiContainer("ForEachRow", new BodyDescriptor(BodyShape.TypedAction, "Body", "System.Data.DataRow", "CurrentRow"),
            props: [L("DisplayName"), E("DataTable")]),
        UiLeaf("LogMessage", L("DisplayName"), E("Message"), Enum("Level", "Trace", "Info", "Warn", "Error", "Fatal")),
        UiLeaf("InvokeWorkflowFile", L("DisplayName"), L("WorkflowFileName", required: true)),
        UiContainer("RetryScope", new BodyDescriptor(BodyShape.UntypedAction, "ActivityBody"),
            props: [L("DisplayName"), L("NumberOfRetries"), L("RetryInterval"), L("ContinueOnError")]),
        UiLeaf("BuildDataTable", L("DisplayName"), E("DataTable")),
        UiLeaf("AddDataRow", L("DisplayName"), E("DataTable"), E("ArrayRow")),
        // InvokeCode binds its parameters through the .Arguments dictionary and
        // takes no activity body, so it is not a container.
        new ActivitySchema("InvokeCode", Ui.Prefix, Ui.Ns, false,
            [L("DisplayName"), L("Code", required: true), Enum("Language", "VBNet", "CSharp"), L("ContinueOnError")],
            PackageId: SystemPackage, FullTypeName: "UiPath.Core.Activities.InvokeCode",
            Body: new BodyDescriptor(BodyShape.ArgumentDictionary, "Arguments")),

        // ---- classic Excel (COM-interop; standalone, no scope) ----
        UiLeafPackaged(ExcelPackage, "UiPath.Excel.Activities.ReadRange", "ReadRange",
            L("DisplayName"), L("Range"), L("SheetName"), L("WorkbookPath"), E("DataTable"), L("AddHeaders")),
        UiLeafPackaged(ExcelPackage, "UiPath.Excel.Activities.WriteRange", "WriteRange",
            L("DisplayName"), L("Range"), L("SheetName"), L("WorkbookPath"), L("StartingCell"), E("DataTable")),

        // ---- modern Excel (Portable-safe; the family that works in Portable projects) ----
        ModernExcelContainer("ExcelApplicationCard",
            new BodyDescriptor(BodyShape.TypedAction, "Body", "UiPath.Excel.IWorkbookQuickHandle", "Excel"),
            L("DisplayName"), E("WorkbookPath"), L("CreateNewFile"), L("AutoSave"), L("ReadOnly")),
        ModernExcelLeaf("ReadRangeX",
            L("DisplayName"), E("Range"), E("SaveTo"), L("AddHeaders")),
        ModernExcelLeaf("WriteRangeX",
            L("DisplayName"), E("Source"), E("Destination"), L("Append"), L("ExcludeHeaders"), L("IgnoreEmptySource")),
        ModernExcelContainer("ExcelForEachRowX",
            new BodyDescriptor(BodyShape.TypedAction, "Body", "System.Data.DataRow", "CurrentRow"),
            L("DisplayName"), E("Range")),

        // ---- UI Automation (the documented placeholder-selector path) ----
        // The Rule-24 wrap lives in the Body slot; selectors stay unset until a
        // developer runs Indicate, which is why Target is not marked required.
        UiaContainer("NApplicationCard", new BodyDescriptor(BodyShape.ActivityCollection, "Body"),
            L("DisplayName"), L("ApplicationWindow"), Enum("InteractionMode", "HardwareEvents", "Simulate", "DebuggerApi"), L("Target")),
        UiaLeaf("NClick",
            L("DisplayName"), L("Target"), Enum("ClickType", "Single", "Double", "Down", "Up"),
            L("KeyModifiers"), L("WaitForReady"), L("HealingAgentBehavior")),
        UiaLeaf("NDoubleClick",
            L("DisplayName"), L("Target"), L("WaitForReady"), L("HealingAgentBehavior")),
        UiaLeaf("NTypeInto",
            L("DisplayName"), L("Target"), E("Text"), L("ClickBeforeTyping"),
            Enum("EmptyFieldMode", "None", "SingleLine", "MultiLine"), L("WaitForReady"), L("HealingAgentBehavior")),
        UiaLeaf("NGetText",
            L("DisplayName"), L("Target"), E("TextString"), L("HealingAgentBehavior")),
        UiaLeaf("NGoToUrl",
            L("DisplayName"), L("Url"), L("WaitForReady"), L("HealingAgentBehavior")),
    ];

    public static IActivityCatalog Fallback { get; } = new ListActivityCatalog(All, "fallback");

    // Keyed by both the spec name and the emitted element name, so a spec may say
    // either "While" (the toolbox label) or "InterruptibleWhile" (the type Studio
    // serializes). UIA classes carry an "N" prefix, so the short toolbox name
    // ("Click", "TypeInto") resolves to the same schema.
    private static readonly IReadOnlyDictionary<string, ActivitySchema> ByName = BuildLookup(All);

    private static IReadOnlyDictionary<string, ActivitySchema> BuildLookup(IReadOnlyList<ActivitySchema> schemas) {
        var map = new Dictionary<string, ActivitySchema>(StringComparer.OrdinalIgnoreCase);
        foreach (var schema in schemas) {
            map.TryAdd(schema.Name, schema);
            if (!string.Equals(schema.RenderName, schema.Name, StringComparison.OrdinalIgnoreCase)) {
                map.TryAdd(schema.RenderName, schema);
            }

            var render = schema.RenderName;
            if (render.StartsWith('N') && render.Length > 1 && char.IsUpper(render[1])) {
                map.TryAdd(render[1..], schema);
            }
        }

        return map;
    }

    public static bool TryGet(string name, [NotNullWhen(true)] out ActivitySchema? schema) =>
        ByName.TryGetValue(name, out schema);

    public static string? Suggest(string name) => Suggest(name, All);

    public static string? Suggest(string name, IEnumerable<ActivitySchema> schemas) {
        string? best = null;
        var bestDistance = 4;
        foreach (var schema in schemas) {
            var distance = Levenshtein(name, schema.Name);
            if (distance < bestDistance) {
                bestDistance = distance;
                best = schema.Name;
            }
        }

        return best;
    }

    // Framework System.Activities.Statements types: the default activities namespace.
    internal static readonly HashSet<string> WorkflowFoundationNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Sequence", "Assign", "If", "Switch", "TryCatch", "WriteLine", "Delay", "Throw", "Rethrow"
    };

    // UiPath.Core.Activities types: the ui: namespace. Used to classify a
    // discovered activity that did not report one.
    internal static readonly HashSet<string> UiPathCoreNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "While", "DoWhile", "ForEach", "InterruptibleWhile", "InterruptibleDoWhile",
        "ForEachRow", "LogMessage", "InvokeWorkflowFile", "InvokeCode", "RetryScope",
        "BuildDataTable", "AddDataRow", "ReadRange", "WriteRange"
    };

    // Rule 21a's fast-path card: the 13 built-in activities whose surface the
    // hand-written catalog owns. A discovery hit on this list keeps the curated
    // schema and skips the package starter query; anything else asks the package.
    public static readonly IReadOnlySet<string> CommonActivityCard = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Sequence", "If", "Switch", "TryCatch", "While", "DoWhile", "ForEach",
        "Assign", "LogMessage", "WriteLine", "Delay", "Throw", "Rethrow"
    };

    // Activities whose parameters arrive as an <Arguments> scg:Dictionary of
    // In/Out/InOut bindings rather than as an activity body.
    internal static readonly HashSet<string> ArgumentDictionaryActivities = new(StringComparer.OrdinalIgnoreCase)
    {
        "InvokeWorkflowFile", "InvokeCode"
    };

    // Element type per catalog expression property, as an x:TypeArguments token,
    // plus the argument direction. Only properties whose argument type is fixed
    // by the activity are listed; a type that varies with the spec's own
    // TypeArgument property (Assign, Switch, ForEach) is resolved by the builder.
    private static readonly IReadOnlyDictionary<string, (string Token, ArgumentDirection Direction)> ExpressionArguments =
        new Dictionary<string, (string, ArgumentDirection)>(StringComparer.OrdinalIgnoreCase) {
            ["If.Condition"] = ("x:Boolean", ArgumentDirection.In),
            ["While.Condition"] = ("x:Boolean", ArgumentDirection.In),
            ["DoWhile.Condition"] = ("x:Boolean", ArgumentDirection.In),
            ["LogMessage.Message"] = ("x:Object", ArgumentDirection.In),
            ["WriteLine.Text"] = ("x:String", ArgumentDirection.In),
            ["Delay.Duration"] = ("x:TimeSpan", ArgumentDirection.In),
            ["Throw.Exception"] = ("s:Exception", ArgumentDirection.In),
            ["Assign.To"] = (string.Empty, ArgumentDirection.Out),
            ["Assign.Value"] = (string.Empty, ArgumentDirection.In),
            ["Switch.Expression"] = (string.Empty, ArgumentDirection.In),
            ["ForEach.Values"] = (string.Empty, ArgumentDirection.In),
            ["ForEachRow.DataTable"] = ("sd:DataTable", ArgumentDirection.In),
            ["BuildDataTable.DataTable"] = ("sd:DataTable", ArgumentDirection.Out),
            ["AddDataRow.DataTable"] = ("sd:DataTable", ArgumentDirection.InOut),
            ["AddDataRow.ArrayRow"] = ("x:Object[]", ArgumentDirection.In),
            ["ReadRange.DataTable"] = ("sd:DataTable", ArgumentDirection.Out),
            ["WriteRange.DataTable"] = ("sd:DataTable", ArgumentDirection.In),
            ["ExcelApplicationCard.WorkbookPath"] = ("x:String", ArgumentDirection.In),
            ["ReadRangeX.SaveTo"] = ("sd:DataTable", ArgumentDirection.Out),
            ["WriteRangeX.Source"] = ("sd:DataTable", ArgumentDirection.In),
            ["NTypeInto.Text"] = ("x:String", ArgumentDirection.In),
            ["NGetText.TextString"] = ("x:String", ArgumentDirection.Out),
        };

    internal static bool AcceptsArgumentDictionary(string activityName) =>
        ArgumentDictionaryActivities.Contains(activityName);

    /// <summary>
    /// The fixed argument type and direction for an expression property, or null
    /// when the property is not a known expression argument. An empty token means
    /// the type comes from the spec's own <c>TypeArgument</c> property.
    /// </summary>
    internal static (string Token, ArgumentDirection Direction)? ExpressionArgument(string activityName, string propertyName) =>
        ExpressionArguments.TryGetValue($"{activityName}.{propertyName}", out var known) ? known : null;

    private static int Levenshtein(string a, string b) {
        a = a.ToLowerInvariant();
        b = b.ToLowerInvariant();
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) previous[j] = j;

        for (var i = 1; i <= a.Length; i++) {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++) {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(previous[j] + 1, current[j - 1] + 1), previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }
}