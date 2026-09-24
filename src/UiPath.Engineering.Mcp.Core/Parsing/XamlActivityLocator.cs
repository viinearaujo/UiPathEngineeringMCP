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
    string? IdRef,
    /// <summary>
    /// The attached-property slot this activity sits in, when the parent is a
    /// slot-named container (If.Then / If.Else, Switch.Default, a keyed case,
    /// TryCatch.Try / Catch / Finally, ForEach.Body, PickBranch.*,
    /// NApplicationCard.Body). Null for an activity in a plain child list. The
    /// slot is a label only: <see cref="Id"/>, <see cref="Depth"/>, and
    /// <see cref="ParentId"/> are unchanged by it.
    /// </summary>
    string? Slot = null);

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
        Walk(parent, parentId, depth, results, ref ordinal, slot: null);
    }

    private static void Walk(
        XElement parent, string? parentId, int depth, List<LocatedActivity> results, ref int ordinal, string? slot) {
        foreach (var child in parent.Elements()) {
            var local = child.Name.LocalName;
            // Designer state is opaque: the ViewState dictionary holds only
            // keyed property values (av:Point, av:Size, …), never activities, so
            // the whole subtree is skipped instead of being walked for IDs.
            if (XamlWorkflowParser.IsViewStateDictionary(child)) {
                continue;
            }

            if (local.Contains('.') || XamlWorkflowParser.NonActivityElements.Contains(local)) {
                // An attached-property container is transparent for identity: it
                // shares the parent's ordinal counter and depth. Its slot name is
                // carried onto the activities inside it, so Rule 24 wrap detection
                // can tell a wrapped If.Then from a bare one without changing any
                // existing structural-path ID.
                Walk(child, parentId, depth, results, ref ordinal, SlotName(parent, child) ?? slot);
                continue;
            }

            ordinal++;
            var segment = $"{local.ToLowerInvariant()}.{ordinal}";
            var id = parentId is null ? segment : $"{parentId}/{segment}";
            var line = child is IXmlLineInfo info && info.HasLineInfo() ? info.LineNumber : 0;
            // A keyed child of a Switch is one of its cases; the key identifies the slot.
            var activitySlot = slot;
            if (string.Equals(parent.Name.LocalName, "Switch", StringComparison.OrdinalIgnoreCase)
                && child.Attribute(XName.Get("Key", "http://schemas.microsoft.com/winfx/2006/xaml"))?.Value is { Length: > 0 } key) {
                activitySlot = $"Case:{key}";
            }

            results.Add(new LocatedActivity(child, id, parentId, results.Count, line, depth, ReadIdRef(child), activitySlot));
            var childOrdinal = 0;
            Walk(child, id, depth + 1, results, ref childOrdinal, slot: null);
        }
    }

    // The slot name an attached-property container represents. "If.Then" under
    // an If is "Then"; "ForEach.Body" is "Body". A container whose owner is not
    // the parent (ActivityAction under a Catch) keeps the enclosing slot name.
    private static string? SlotName(XElement parent, XElement container) {
        var local = container.Name.LocalName;
        if (local.Equals("Catch", StringComparison.Ordinal)) {
            // A TryCatch catch clause is a slot of its own, inside the
            // TryCatch.Catches collection.
            return "Catch";
        }

        var dot = local.LastIndexOf('.');
        if (dot > 0 && dot < local.Length - 1) {
            var owner = local[..dot];
            var property = local[(dot + 1)..];
            if (owner.Equals(parent.Name.LocalName, StringComparison.OrdinalIgnoreCase)) {
                return property;
            }
        }

        return null;
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

    public static bool MatchesAddress(LocatedActivity activity, string activityId) =>
        string.Equals(activity.Id, activityId, StringComparison.Ordinal)
        || (!string.IsNullOrWhiteSpace(activity.IdRef)
            && string.Equals(activity.IdRef, activityId, StringComparison.Ordinal));
}
