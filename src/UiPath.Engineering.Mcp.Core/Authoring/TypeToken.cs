namespace UiPath.Engineering.Mcp.Core.Authoring;

// Shared type-token rule for x:TypeArguments rendering: the four BCL
// primitives (String/Int32/Boolean/Double) get an x: prefix; every other
// bare type name (e.g. "DataRow") passes through unqualified, and tokens
// already qualified with '.' or ':' pass through verbatim. Both XamlBuilder
// and WorkflowSurfaceEditor use this so the same user input renders the
// same XAML regardless of the tool.
internal static class TypeToken
{
    public static string Render(string type) {
        var trimmed = type.Trim();
        if (trimmed.Contains(':') || trimmed.Contains('.')) {
            return trimmed;
        }
        return trimmed is "String" or "Int32" or "Boolean" or "Double" ? "x:" + trimmed : trimmed;
    }
}
