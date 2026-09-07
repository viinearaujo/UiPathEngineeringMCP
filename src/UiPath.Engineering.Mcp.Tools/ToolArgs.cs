using System.Diagnostics;
using UiPath.Engineering.Mcp.Core;
using UiPath.Engineering.Mcp.Core.Models;

namespace UiPath.Engineering.Mcp.Tools;

internal static class ToolArgs {
    public static ToolResult? ParseChoice(
        string? value,
        string paramName,
        IReadOnlyList<string> accepted,
        Stopwatch sw,
        out string parsed) {
        var candidate = (value ?? string.Empty).Trim();
        string? match = null;
        foreach (var option in accepted) {
            if (string.Equals(option, candidate, StringComparison.OrdinalIgnoreCase)) {
                match = option;
                break;
            }
        }

        if (match is not null) {
            parsed = match;
            return null;
        }

        parsed = candidate;
        var listed = string.Join(", ", accepted.Select(a => $"'{a}'"));
        return ToolResults.Failure(new ToolError(
            ToolErrorCodes.InvalidArgument,
            $"{paramName} must be one of: {listed}.",
            $"Pass {paramName} as one of: {listed}."), sw);
    }

    public static ToolResult? ParseEnum<TEnum>(
        string? value,
        string paramName,
        Stopwatch sw,
        out TEnum parsed)
        where TEnum : struct, Enum {
        parsed = default;
        var names = Enum.GetNames<TEnum>();
        var candidate = value?.Trim();
        if (Enum.TryParse(candidate, ignoreCase: true, out parsed)
            && names.Any(n => string.Equals(n, candidate, StringComparison.OrdinalIgnoreCase))) {
            return null;
        }

        var listed = string.Join(", ", names.Select(n => $"'{n}'"));
        return ToolResults.Failure(new ToolError(
            ToolErrorCodes.InvalidArgument,
            $"{paramName} must be one of: {listed}.",
            $"Pass {paramName} as one of: {listed}."), sw);
    }
}
