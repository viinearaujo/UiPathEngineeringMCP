using System.Text.Json;

namespace UiPath.Engineering.Mcp.Tools;

internal static class McpToolErrorMapper {
    public const string ErrorStatus = "error";

    public static bool IsErrorStatus(string? status) =>
        string.Equals(status, ErrorStatus, StringComparison.OrdinalIgnoreCase);

    public static bool StructuredContentIndicatesError(JsonElement structuredContent) {
        if (structuredContent.ValueKind != JsonValueKind.Object) {
            return false;
        }

        foreach (var property in structuredContent.EnumerateObject()) {
            if (property.NameEquals("status") || property.NameEquals("Status")) {
                return property.Value.ValueKind == JsonValueKind.String
                    && IsErrorStatus(property.Value.GetString());
            }
        }

        return false;
    }

    public static bool StructuredContentIndicatesError(object? structuredContent) =>
        structuredContent switch {
            JsonElement element => StructuredContentIndicatesError(element),
            JsonDocument document => StructuredContentIndicatesError(document.RootElement),
            _ => false
        };
}
