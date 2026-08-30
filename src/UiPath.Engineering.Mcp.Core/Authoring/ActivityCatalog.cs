using System.Diagnostics.CodeAnalysis;

namespace UiPath.Engineering.Mcp.Core.Authoring;

public static class ActivityCatalog
{
    private static readonly (string Prefix, string Ns) Wf = ("", "http://schemas.microsoft.com/netfx/2009/xaml/activities");
    private static readonly (string Prefix, string Ns) Ui = ("ui", "http://schemas.uipath.com/workflow/activities");

    private static readonly HashSet<string> ExcelActivities = new(StringComparer.OrdinalIgnoreCase)
    {
        "ForEachRow", "ReadRange", "WriteRange"
    };

    private static ActivitySchema S(string name, (string Prefix, string Ns) ns, bool container, params PropertySchema[] props) =>
        new(name, ns.Prefix, ns.Ns, container, props,
            PackageId: ExcelActivities.Contains(name) ? "UiPath.Excel.Activities" : "UiPath.System.Activities");

    private static PropertySchema E(string name, bool required = true) => new(name, required, PropertyKind.Expression);
    private static PropertySchema L(string name, bool required = false) => new(name, required, PropertyKind.Literal);
    private static PropertySchema T(string name, bool required = true) => new(name, required, PropertyKind.TypeArgument);

    public static IReadOnlyList<ActivitySchema> All { get; } =
    [
        S("Sequence",   Wf, true,  L("DisplayName")),
        S("Assign",     Wf, false, L("DisplayName"), E("To"), E("Value")),
        S("If",         Wf, true,  L("DisplayName"), E("Condition")),
        S("Switch",     Wf, true,  L("DisplayName"), E("Expression"), T("TypeArgument")),
        S("ForEach",    Wf, true,  L("DisplayName"), E("Values"), T("TypeArgument"), L("ItemName")),
        S("ForEachRow", Ui, true,  L("DisplayName"), E("DataTable")),
        S("While",      Wf, true,  L("DisplayName"), E("Condition")),
        S("DoWhile",    Wf, true,  L("DisplayName"), E("Condition")),
        S("TryCatch",   Wf, true,  L("DisplayName")),
        S("LogMessage", Ui, false, L("DisplayName"), E("Message"), L("Level")),
        S("WriteLine",  Wf, false, L("DisplayName"), E("Text")),
        S("InvokeWorkflowFile", Ui, false, L("DisplayName"), L("WorkflowFileName", required: true)),
        S("Delay",      Wf, false, L("DisplayName"), E("Duration")),
        S("Throw",      Wf, false, L("DisplayName"), E("Exception")),
        S("Rethrow",    Wf, false, L("DisplayName")),
        S("RetryScope", Ui, true,  L("DisplayName"), L("NumberOfRetries"), L("RetryInterval")),
        S("BuildDataTable", Ui, false, L("DisplayName"), E("DataTable")),
        S("AddDataRow", Ui, false, L("DisplayName"), E("DataTable"), E("ArrayRow")),
        S("ReadRange",  Ui, false, L("DisplayName"), L("Range"), L("SheetName"), E("DataTable")),
        S("WriteRange", Ui, false, L("DisplayName"), L("Range"), L("SheetName"), E("DataTable")),
        S("InvokeCode", Ui, true,  L("DisplayName"), L("Code", required: true), L("Language")),
    ];

    public static IActivityCatalog Fallback { get; } = new ListActivityCatalog(All, "fallback");

    private static readonly IReadOnlyDictionary<string, ActivitySchema> ByName =
        All.ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);

    public static bool TryGet(string name, [NotNullWhen(true)] out ActivitySchema? schema) =>
        ByName.TryGetValue(name, out schema);

    public static string? Suggest(string name) => Suggest(name, All);

    public static string? Suggest(string name, IEnumerable<ActivitySchema> schemas)
    {
        string? best = null;
        var bestDistance = 4;
        foreach (var schema in schemas)
        {
            var distance = Levenshtein(name, schema.Name);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = schema.Name;
            }
        }

        return best;
    }

    internal static readonly HashSet<string> WorkflowFoundationNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Sequence", "Assign", "If", "Switch", "ForEach", "While", "DoWhile",
        "TryCatch", "WriteLine", "Delay", "Throw", "Rethrow"
    };

    private static int Levenshtein(string a, string b)
    {
        a = a.ToLowerInvariant();
        b = b.ToLowerInvariant();
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) previous[j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(previous[j] + 1, current[j - 1] + 1), previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }
}
