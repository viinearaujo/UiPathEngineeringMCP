using System.Xml;
using System.Xml.Linq;

namespace UiPath.Engineering.Mcp.Core.Parsing;

/// <summary>
/// One activity located in a parsed XAML document, with its computed structural-path ID
/// (e.g. "sequence.1/if.1/logmessage.2"). Each path segment is the lowercased element
/// local name plus a 1-based ordinal counted in document order among the activity
/// siblings of one child-list traversal; attached-property containers (dot-suffixed
/// local names) and XAML primitives are transparent — recursed without consuming a
/// segment or depth, starting a fresh ordinal counter for their own child list.
/// IDs are deterministic per parse snapshot; structural edits may shift ordinals.
/// </summary>
public sealed record LocatedActivity(
    XElement Element,
    string Id,
    string? ParentId,
    int Order,
    int Line,
    int Depth);

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
        foreach (var child in parent.Elements()) {
            var local = child.Name.LocalName;
            if (local.Contains('.') || XamlWorkflowParser.NonActivityElements.Contains(local)) {
                WalkChildren(child, parentId, depth, results);
                continue;
            }

            ordinal++;
            var segment = $"{local.ToLowerInvariant()}.{ordinal}";
            var id = parentId is null ? segment : $"{parentId}/{segment}";
            var line = child is IXmlLineInfo info && info.HasLineInfo() ? info.LineNumber : 0;
            results.Add(new LocatedActivity(child, id, parentId, results.Count, line, depth));
            WalkChildren(child, id, depth + 1, results);
        }
    }
}
