namespace UiPath.Engineering.Mcp.Core.Authoring;

// Shared type-token rule for x:TypeArguments rendering. A bare type name is
// mapped onto the XML namespace alias that can actually resolve it:
//
//   * the eleven primitives registered in the XAML language schema get "x:"
//     (String, Int32, Int64, Double, Boolean, Byte, Single, Decimal, Char,
//     Object, TimeSpan);
//   * other System types get "s:" (DateTime, Guid, Exception, …);
//   * System.Data types get "sd:" (DataTable, DataRow, …);
//   * anything else (e.g. an unqualified "Invoice") passes through unchanged,
//     and tokens already qualified with '.' or ':' pass through verbatim.
//
// "x:" only resolves the primitives listed above — "x:DateTime" fails at load
// with "Cannot create unknown type", so DateTime/Guid/TimeSpan-like types must
// not be guessed into it. Callers that emit a token are responsible for
// declaring the alias it uses (XamlBuilder does this via TypeToken.AliasOf).
// Both XamlBuilder and WorkflowSurfaceEditor use this so the same user input
// renders the same XAML regardless of the tool.
internal static class TypeToken {
    // The complete set of types the XAML language schema registers under "x:".
    internal static readonly IReadOnlySet<string> XamlPrimitives = new HashSet<string>(StringComparer.Ordinal) {
        "String", "Int32", "Int64", "Double", "Boolean", "Byte",
        "Single", "Decimal", "Char", "Object", "TimeSpan"
    };

    // System types that are NOT XAML primitives; they need xmlns:s.
    private static readonly IReadOnlySet<string> SystemTypes = new HashSet<string>(StringComparer.Ordinal) {
        "DateTime", "DateTimeOffset", "Guid", "Uri", "Exception", "Type",
        "DBNull", "Nullable", "Array", "Void", "Attribute"
    };

    // System.Data types; they need xmlns:sd.
    private static readonly IReadOnlySet<string> DataTypes = new HashSet<string>(StringComparer.Ordinal) {
        "DataTable", "DataRow", "DataColumn", "DataSet", "DataView", "DataRelation"
    };

    internal const string SystemAlias = "s";
    internal const string DataAlias = "sd";

    public static string Render(string type) {
        var trimmed = type.Trim();
        if (trimmed.Length == 0 || trimmed.Contains(':') || trimmed.Contains('.')) {
            return trimmed;
        }

        if (XamlPrimitives.Contains(trimmed)) {
            return "x:" + trimmed;
        }

        if (SystemTypes.Contains(trimmed)) {
            return SystemAlias + ":" + trimmed;
        }

        return DataTypes.Contains(trimmed) ? DataAlias + ":" + trimmed : trimmed;
    }

    // The xmlns alias a rendered token depends on, or null when the token needs
    // no extra declaration ("x:" is always declared; unprefixed tokens resolve
    // against the default activities namespace).
    internal static string? AliasOf(string renderedToken) {
        var colon = renderedToken.IndexOf(':');
        if (colon <= 0 || colon == renderedToken.Length - 1) {
            return null;
        }

        var prefix = renderedToken[..colon];
        return prefix is "x" or "xml" or "xmlns" ? null : prefix;
    }

    // Aliases used by a comma-separated x:TypeArguments value ("x:String, Argument").
    internal static IEnumerable<string> AliasesIn(string? typeArguments) {
        if (string.IsNullOrWhiteSpace(typeArguments)) {
            yield break;
        }

        foreach (var part in typeArguments.Split(',')) {
            if (AliasOf(part.Trim()) is { } alias) {
                yield return alias;
            }
        }
    }
}
