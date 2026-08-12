using System.Xml;
using System.Xml.Linq;

namespace UiPath.Engineering.Mcp.Core.Parsing;

/// <summary>
/// Activity-level edits on a UiPath .xaml workflow: insert an activity fragment into a
/// container, replace an activity, or remove one. Targets are matched by DisplayName
/// (optionally narrowed by activity type). Whitespace is preserved so untouched regions
/// of the file stay byte-identical; edits never throw, failures come back as
/// <see cref="XamlEditResult.Error"/>.
/// </summary>
public static class XamlActivityEditor {
    public const string Insert = "insert";
    public const string Replace = "replace";
    public const string Remove = "remove";

    public const string First = "first";
    public const string Last = "last";

    // Namespaces commonly used inside UiPath workflow bodies, so fragments can use
    // unprefixed WF activities plus the ui:/x: prefixes without repeating declarations.
    private const string FragmentWrapperNamespaces =
        "xmlns=\"http://schemas.microsoft.com/netfx/2009/xaml/activities\" " +
        "xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" " +
        "xmlns:ui=\"http://schemas.uipath.com/workflow/activities\" " +
        "xmlns:mc=\"http://schemas.openxmlformats.org/markup-compatibility/2006\" " +
        "xmlns:sap=\"http://schemas.microsoft.com/netfx/2009/xaml/activities/presentation\" " +
        "xmlns:sap2010=\"http://schemas.microsoft.com/netfx/2010/xaml/activities/presentation\"";

    public static XamlEditResult Edit(
        string xamlContent,
        string operation,
        string displayName,
        string? activityType = null,
        string? fragment = null,
        string position = Last) {
        if (string.IsNullOrWhiteSpace(displayName)) {
            return XamlEditResult.Failure("displayName is required to locate the target activity.");
        }

        XDocument doc;
        try {
            doc = XDocument.Parse(xamlContent, LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);
        } catch (Exception ex) when (ex is XmlException or InvalidOperationException) {
            return XamlEditResult.Failure($"XAML parse failure: {ex.Message}");
        }

        var matches = XamlActivityLocator.Locate(doc)
            .Where(a => string.Equals(a.Element.Attribute("DisplayName")?.Value, displayName, StringComparison.Ordinal)
                && (activityType is null || string.Equals(a.Element.Name.LocalName, activityType, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (matches.Count == 0) {
            return XamlEditResult.Failure(
                $"No activity found with DisplayName '{displayName}'" +
                (activityType is null ? "." : $" of type '{activityType}'."),
                ToolErrorCodes.ActivityNotFound);
        }
        if (matches.Count > 1) {
            return XamlEditResult.Failure(
                $"Found {matches.Count} activities with DisplayName '{displayName}'. " +
                "Pass activityId to target exactly one (run find_activity to list IDs), " +
                "or narrow with activityType.",
                ToolErrorCodes.AmbiguousActivity);
        }

        return ApplyEdit(doc, matches[0], operation, fragment, position);
    }

    /// <summary>
    /// Edit the activity addressed by a structural ID previously issued by find_activity.
    /// When <paramref name="activityType"/> or <paramref name="expectedDisplayName"/> are
    /// supplied they are verified against the resolved element; a mismatch means the file
    /// changed since the ID was issued and the edit is refused with ACTIVITY_ID_STALE.
    /// </summary>
    public static XamlEditResult EditById(
        string xamlContent,
        string operation,
        string activityId,
        string? activityType = null,
        string? expectedDisplayName = null,
        string? fragment = null,
        string position = Last) {
        if (string.IsNullOrWhiteSpace(activityId)) {
            return XamlEditResult.Failure("activityId is required to locate the target activity.",
                ToolErrorCodes.InvalidArgument);
        }

        XDocument doc;
        try {
            doc = XDocument.Parse(xamlContent, LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);
        } catch (Exception ex) when (ex is XmlException or InvalidOperationException) {
            return XamlEditResult.Failure($"XAML parse failure: {ex.Message}");
        }

        var located = XamlActivityLocator.Locate(doc)
            .FirstOrDefault(a => string.Equals(a.Id, activityId, StringComparison.Ordinal));
        if (located is null) {
            return XamlEditResult.Failure($"No activity found with ID '{activityId}'.",
                ToolErrorCodes.ActivityNotFound);
        }

        // Verify the snapshot still matches reality: an ID issued before a structural
        // edit may now resolve to a different activity than the caller intended.
        var local = located.Element.Name.LocalName;
        if (activityType is not null && !string.Equals(local, activityType, StringComparison.OrdinalIgnoreCase)) {
            return XamlEditResult.Failure(
                $"Activity ID '{activityId}' now resolves to a '{local}', not '{activityType}'. " +
                "The file changed since the ID was issued; re-run find_activity for fresh IDs.",
                ToolErrorCodes.ActivityIdStale);
        }
        var actualDisplayName = located.Element.Attribute("DisplayName")?.Value;
        if (expectedDisplayName is not null
            && !string.Equals(actualDisplayName, expectedDisplayName, StringComparison.Ordinal)) {
            return XamlEditResult.Failure(
                $"Activity ID '{activityId}' now resolves to DisplayName '{actualDisplayName}', not '{expectedDisplayName}'. " +
                "The file changed since the ID was issued; re-run find_activity for fresh IDs.",
                ToolErrorCodes.ActivityIdStale);
        }

        return ApplyEdit(doc, located, operation, fragment, position);
    }

    private static XamlEditResult ApplyEdit(
        XDocument doc, LocatedActivity target, string operation, string? fragment, string position) {
        switch (operation) {
            case Remove:
                RemoveElement(target.Element);
                break;

            case Insert:
            case Replace:
                var nodes = ParseFragment(fragment, out var fragmentError);
                if (nodes is null) {
                    return XamlEditResult.Failure(fragmentError!);
                }
                if (operation == Insert) {
                    InsertInto(target.Element, nodes, position == First);
                } else {
                    target.Element.ReplaceWith(nodes);
                }
                break;

            default:
                return XamlEditResult.Failure($"Unknown operation '{operation}'. Use insert, replace, or remove.");
        }

        return XamlEditResult.Ok(Serialize(doc), 1, target.Id);
    }

    private static List<XElement>? ParseFragment(string? fragment, out string? error) {
        if (string.IsNullOrWhiteSpace(fragment)) {
            error = "fragment is required for insert and replace operations.";
            return null;
        }

        try {
            var wrapped = XDocument.Parse($"<Wrapper {FragmentWrapperNamespaces}>{fragment}</Wrapper>",
                LoadOptions.PreserveWhitespace);

            // prefix -> namespace from the wrapper, so inserted nodes can re-declare the
            // prefixes they use (UiPath style: namespaces declared at point of use).
            // The default-namespace declaration (bare "xmlns") is excluded: unprefixed
            // fragment elements resolve against the target document root instead.
            var prefixes = wrapped.Root!.Attributes()
                .Where(a => a.IsNamespaceDeclaration && a.Name.Namespace == XNamespace.Xmlns)
                .ToDictionary(a => (XNamespace)a.Value, a => a.Name.LocalName);

            var nodes = wrapped.Root!.Elements().ToList();
            if (nodes.Count == 0) {
                error = "fragment did not contain any activity element.";
                return null;
            }
            foreach (var node in nodes) {
                node.Remove();
                ApplyFragmentPrefixes(node, prefixes);
            }
            error = null;
            return nodes;
        } catch (Exception ex) when (ex is XmlException or InvalidOperationException) {
            error = $"fragment is not valid XAML: {ex.Message}";
            return null;
        }
    }

    // Detached fragments have no namespace scope; without a declared prefix the serializer
    // would redefine the default xmlns on the element. Re-declare each used non-default
    // namespace with its conventional prefix (e.g. xmlns:ui="...") on the fragment root.
    // Redundant declarations (prefix already in scope in the target document) are dropped
    // by the serializer.
    private static void ApplyFragmentPrefixes(XElement node, Dictionary<XNamespace, string> prefixes) {
        var used = node.DescendantsAndSelf()
            .SelectMany(e => e.Attributes()
                .Where(a => !a.IsNamespaceDeclaration)
                .Select(a => a.Name.Namespace)
                .Append(e.Name.Namespace))
            .Where(ns => ns != XNamespace.None)
            .Distinct();

        foreach (var ns in used) {
            if (!prefixes.TryGetValue(ns, out var prefix) || prefix.Length == 0) {
                continue; // default WF namespace resolves against the target document root
            }
            node.SetAttributeValue(XNamespace.Xmlns + prefix, ns.NamespaceName);
        }
    }

    private static void InsertInto(XElement target, List<XElement> nodes, bool first) {
        var ownIndent = GetIndent(target);
        var childIndent = DetectChildIndent(target) ?? ownIndent + "  ";

        if (first && target.FirstNode is not null) {
            var anchor = target.FirstNode;
            foreach (var node in nodes) {
                anchor.AddBeforeSelf(new XText("\n" + childIndent), node);
            }
            return;
        }

        // Append: drop the whitespace that currently pads the closing tag, then rebuild it.
        if (target.LastNode is XText trailing && string.IsNullOrWhiteSpace(trailing.Value)) {
            trailing.Remove();
        }
        foreach (var node in nodes) {
            target.Add(new XText("\n" + childIndent), node);
        }
        target.Add(new XText("\n" + ownIndent));
    }

    private static void RemoveElement(XElement element) {
        // Also drop the indentation text in front of the element so no blank line remains.
        if (element.PreviousNode is XText leading && string.IsNullOrWhiteSpace(leading.Value)) {
            leading.Remove();
        }
        element.Remove();
    }

    private static string GetIndent(XElement element) {
        if (element.PreviousNode is XText text) {
            var value = text.Value;
            var lastNewline = value.LastIndexOf('\n');
            if (lastNewline >= 0) {
                return value[(lastNewline + 1)..];
            }
        }
        return string.Empty;
    }

    private static string? DetectChildIndent(XElement target) {
        var firstChild = target.Elements().FirstOrDefault();
        return firstChild is null ? null : GetIndent(firstChild);
    }

    private static string Serialize(XDocument doc) {
        var settings = new XmlWriterSettings {
            Indent = false,
            OmitXmlDeclaration = doc.Declaration is null,
            Encoding = System.Text.Encoding.UTF8
        };
        using var writer = new StringWriterWithEncoding(System.Text.Encoding.UTF8);
        using (var xml = XmlWriter.Create(writer, settings)) {
            doc.Save(xml);
        }
        return writer.ToString();
    }

    // XmlWriter picks the encoding from the TextWriter; StringWriter reports UTF-16,
    // which would rewrite the declaration to utf-16 while callers write UTF-8 files.
    private sealed class StringWriterWithEncoding : StringWriter {
        private readonly System.Text.Encoding _encoding;
        public StringWriterWithEncoding(System.Text.Encoding encoding) => _encoding = encoding;
        public override System.Text.Encoding Encoding => _encoding;
    }
}

public sealed record XamlEditResult(
    bool Success, string? UpdatedContent, string? Error, int MatchCount,
    string? ErrorCode = null, string? ResolvedId = null) {
    public static XamlEditResult Ok(string content, int matchCount, string? resolvedId = null) =>
        new(true, content, null, matchCount, null, resolvedId);
    public static XamlEditResult Failure(string error, string? errorCode = null) =>
        new(false, null, error, 0, errorCode);
}
