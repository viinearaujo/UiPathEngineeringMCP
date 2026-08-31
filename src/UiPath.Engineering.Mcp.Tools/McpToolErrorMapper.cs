using System.Text.Json;
using UiPath.Engineering.Mcp.Core;
using UiPath.Engineering.Mcp.Core.Models;

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

    // Maps unexpected exceptions to stable ToolError codes. Never copies ex.Message
    // into the client-facing payload.
    public static ToolError ToToolError(Exception ex, string failureSummary) => ex switch {
        FileNotFoundException => new ToolError(
            ToolErrorCodes.ProjectJsonNotFound,
            "The directory is not a UiPath project (project.json is missing).",
            "Pass a UiPath project directory that contains project.json."),
        JsonException => new ToolError(
            ToolErrorCodes.ProjectJsonInvalid,
            "project.json could not be parsed.",
            "Fix the JSON in project.json and retry."),
        UnauthorizedAccessException => new ToolError(
            ToolErrorCodes.PathNotAllowed,
            "The requested path is not accessible.",
            "Pass a path inside Projects:AllowedRoots that this process can read."),
        _ => new ToolError(
            ToolErrorCodes.OperationFailed,
            failureSummary,
            "Check the server logs for details, then retry.")
    };
}
