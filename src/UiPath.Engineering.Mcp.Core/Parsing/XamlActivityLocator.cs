using System.Xml;
using System.Xml.Linq;

namespace UiPath.Engineering.Mcp.Core.Parsing;

/// <summary>
/// One activity located in a parsed XAML document, with its computed structural-path ID
/// (e.g. "sequence.1/if.1/logmessage.2"). Each path segment is the lowercased element
/// local name plus a 1-based ordinal counted in document order among the activity
/// siblings of one child-list traversal; attached-property containers (dot-suffixed
/// local names) and XAML primitives are transparent — recursed without consuming a
/// segment or depth, sharing the parent ordinal counter so Then/Else and Try/Catch
/// cannot mint the same id. IDs are deterministic per parse snapshot; structural
/// edits may shift ordinals.
/// </summary>
public sealed record LocatedActivity(
    XElement Element,
    string Id,
    string? ParentId,
    int Order,
    int Line,
    int Depth,
    string? IdRef);

/// <summary>
/// Single traversal that classifies elements and assigns activity IDs. Both
/// XamlWorkflowParser and XamlActivityEditor consume this so an ID reported by
/// find_activity always addresses the same element the editor edits.
/// </summary>
public static class XamlActivityLocator {
    public static IReadOnlyList<LocatedActivity> Locate(XDocument doc) {
        var results = new List<LocatedActivity>();
        if (doc.Root is not null) {
            WalkChildren(doc.Root, parentId: null, depth: 0, results);
        }
        return results;
    }

    private static void WalkChildren(XElement parent, string? parentId, int depth, List<LocatedActivity> results) {
        var ordinal = 0;
        Walk(parent, parentId, depth, results, ref ordinal);
    }

    private static void Walk(
        XElement parent, string? parentId, int depth, List<LocatedActivity> results, ref int ordinal) {
        foreach (var child in parent.Elements()) {
            var local = child.Name.LocalName;
            if (local.Contains('.') || XamlWorkflowParser.NonActivityElements.Contains(local)) {
                Walk(child, parentId, depth, results, ref ordinal);
                continue;
            }

            ordinal++;
            var segment = $"{local.ToLowerInvariant()}.{ordinal}";
            var id = parentId is null ? segment : $"{parentId}/{segment}";
            var line = child is IXmlLineInfo info && info.HasLineInfo() ? info.LineNumber : 0;
            results.Add(new LocatedActivity(child, id, parentId, results.Count, line, depth, ReadIdRef(child)));
            var childOrdinal = 0;
            Walk(child, id, depth + 1, results, ref childOrdinal);
        }
    }

    // sap2010:WorkflowViewState.IdRef (LocalName "WorkflowViewState.IdRef") is the value
    // uip rpa validate / build JSON diagnostics use to name an activity.
    private static string? ReadIdRef(XElement element) {
        foreach (var attr in element.Attributes()) {
            var local = attr.Name.LocalName;
            if (local.Equals("IdRef", StringComparison.Ordinal)
                || local.EndsWith(".IdRef", StringComparison.Ordinal)) {
                return string.IsNullOrWhiteSpace(attr.Value) ? null : attr.Value;
            }
        }

        return null;
    }
}
