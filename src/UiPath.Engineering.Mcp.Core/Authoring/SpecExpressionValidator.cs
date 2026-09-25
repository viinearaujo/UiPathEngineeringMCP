namespace UiPath.Engineering.Mcp.Core.Authoring;

/// <summary>
/// Expression-form checks for the project's expression language: VB <c>[bracket]</c>
/// shorthand vs raw C# expressions, literals, and type-argument tokens.
/// </summary>
public static class SpecExpressionValidator {
    // An argument binding is an expression in the project language: an Out/InOut
    // binding must be a writable reference (a variable or argument name), never a
    // constructed value.
    public static string? ArgumentFormMismatch(string? value, SpecValidationContext context) {
        if (string.IsNullOrEmpty(value)) {
            return null;
        }

        if (context.Settings.IsCSharp && ExpressionValue.IsBracketWrapped(value)) {
            return $"value \"{value}\" uses VB [bracket] shorthand, which this project does not use.";
        }

        return null;
    }

    public static string CorrectArgumentForm(SpecValidationContext context) => context.Settings.IsCSharp
        ? "pass a raw C# expression with no brackets, e.g. \"value\": \"filePath\" (an Out/InOut binding must be a variable or argument name)."
        : "pass the expression in VB [brackets], e.g. \"value\": \"[filePath]\" (an Out/InOut binding must be a variable or argument name).";

    public static string? AllowedValueMismatch(PropertySchema property, string value) {
        if (property.AllowedValues is not { Count: > 0 } allowed) {
            return null;
        }

        // A literal property holding an expression is already reported as a form
        // mismatch; do not pile a second error on the same value.
        var candidate = value.Trim();
        return allowed.Any(a => string.Equals(a, candidate, StringComparison.OrdinalIgnoreCase))
            ? null
            : $"value \"{value}\" is not one of the allowed values.";
    }

    public static string? FormMismatch(PropertySchema property, string value, SpecValidationContext context) {
        var wrapped = ExpressionValue.IsBracketWrapped(value);
        // The bracket shorthand is VB-only: in a C# project it deserializes as a
        // VisualBasicValue, so C#-only syntax (null, ?., ??, typeof) breaks.
        if (context.Settings.IsCSharp && property.Kind != PropertyKind.Literal && wrapped) {
            return $"value \"{value}\" uses VB [bracket] shorthand, which this C#-expression project does not use.";
        }

        return property.Kind switch {
            PropertyKind.Expression when !context.Settings.IsCSharp && !wrapped && !ExpressionValue.IsQuotedStringLiteral(value) =>
                $"value \"{value}\" is not expression-wrapped.",
            PropertyKind.Literal when wrapped =>
                $"value \"{value}\" is expression-wrapped but the property is a literal.",
            PropertyKind.TypeArgument when value.Contains('[') || value.Contains(']') =>
                $"value \"{value}\" contains brackets but the property is a type name.",
            _ => null,
        };
    }

    public static string CorrectForm(PropertySchema property, SpecValidationContext context) => property.Kind switch {
        PropertyKind.Expression when context.Settings.IsCSharp =>
            $"pass a raw C# expression with no brackets, e.g. \"{property.Name}\": \"{CSharpExample(property.Name)}\" (a quoted C# string literal like \"\\\"text\\\"\" is also accepted).",
        PropertyKind.Expression =>
            $"wrap a VB expression in brackets, e.g. \"{property.Name}\": \"[myVar + 1]\" (a quoted VB string literal like \"\\\"text\\\"\" is also accepted).",
        PropertyKind.Literal =>
            $"pass the raw value with no brackets, e.g. \"{property.Name}\": \"Info\".",
        PropertyKind.TypeArgument =>
            $"pass a type name with no brackets, e.g. \"{property.Name}\": \"DataRow\".",
        _ => property.Name,
    };

    private static string CSharpExample(string propertyName) => propertyName.ToLowerInvariant() switch {
        "to" => "myVar",
        "condition" => "count > 0",
        _ => "myVar + 1"
    };
}
