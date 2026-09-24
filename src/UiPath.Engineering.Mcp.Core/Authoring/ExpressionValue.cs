using System.Globalization;

namespace UiPath.Engineering.Mcp.Core.Authoring;

/// <summary>
/// The value-form rules both <see cref="SpecValidator"/> and
/// <see cref="XamlBuilder"/> apply to spec property values, so a value that
/// validates is a value that renders.
/// </summary>
/// <remarks>
/// Two forms matter:
/// <list type="bullet">
/// <item><description><c>[expr]</c> is VisualBasic bracket shorthand. It deserializes
/// as <c>VisualBasicValue&lt;T&gt;</c> regardless of the project's expression
/// language, so it is only correct in a VisualBasic project.</description></item>
/// <item><description>A converter literal (quoted string, number, boolean,
/// <c>TimeSpan</c>, <c>{x:Null}</c>) is handled by a type converter and never
/// reaches the expression parser — so attribute form is safe for it in either
/// language. Everything else needs a typed <c>CSharpValue</c>/<c>CSharpReference</c>
/// property element in a C# project.</description></item>
/// </list>
/// </remarks>
internal static class ExpressionValue {
    public static bool IsBracketWrapped(string value) {
        var trimmed = value.Trim();
        return trimmed.Length >= 2 && trimmed.StartsWith('[') && trimmed.EndsWith(']');
    }

    // A "…" value is already a valid string-literal expression in both languages.
    public static bool IsQuotedStringLiteral(string value) {
        var trimmed = value.Trim();
        return trimmed.Length >= 2 && trimmed.StartsWith('"') && trimmed.EndsWith('"');
    }

    /// <summary>
    /// True when the value may stay in XAML attribute form in a <b>C#</b> project:
    /// a number, a boolean, a <c>TimeSpan</c> literal or <c>{x:Null}</c> — the
    /// values with a direct type converter that never reaches the expression
    /// parser. A quoted C# string literal is excluded on purpose: the attribute
    /// converter does not strip C# quote characters, so <c>"yes"</c> must render
    /// through a <c>CSharpValue</c> (which evaluates it as C#) rather than as a
    /// literal that keeps the quotes.
    /// </summary>
    public static bool IsCSharpAttributeLiteral(string value) {
        var trimmed = value.Trim();
        if (trimmed.Length == 0 || trimmed.Equals("{x:Null}", StringComparison.Ordinal)) {
            return true;
        }

        return double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out _)
            || bool.TryParse(trimmed, out _)
            || TimeSpan.TryParse(trimmed, CultureInfo.InvariantCulture, out _);
    }

    // [expr] is VB bracket shorthand; a C# binding carries the raw expression.
    public static string Unwrap(string value) {
        var trimmed = value.Trim();
        return IsBracketWrapped(trimmed) ? trimmed[1..^1].Trim() : trimmed;
    }
}


