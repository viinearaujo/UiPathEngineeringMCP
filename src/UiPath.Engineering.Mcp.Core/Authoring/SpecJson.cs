using System.Text.Json;
using UiPath.Engineering.Mcp.Core.Models;

namespace UiPath.Engineering.Mcp.Core.Authoring;

// Shared deserialization of a JSON activity spec so every tool that accepts a
// spec reports malformed JSON and empty specs identically.
internal static class SpecJson {
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    // Returns true when a usable spec was produced; otherwise error is set.
    public static bool TryDeserialize(string json, out ActivitySpec? spec, out ToolError? error) {
        try {
            spec = JsonSerializer.Deserialize<ActivitySpec>(json, JsonOptions);
        } catch (JsonException ex) {
            spec = null;
            error = new ToolError(
                ToolErrorCodes.SpecInvalidSpecJson,
                $"The spec is not valid JSON: {ex.Message}",
                "Pass a JSON object like { \"name\": \"Sequence\", \"children\": [...] }.");
            return false;
        }

        if (spec is null) {
            error = new ToolError(
                ToolErrorCodes.SpecEmptySpec,
                "The activity spec is empty: 'name' is missing or blank.",
                "Provide a spec with a 'name' matching an activity from the catalog, e.g. { \"name\": \"Sequence\", \"children\": [...] }.",
                "validate_activity_spec");
            return false;
        }

        error = null;
        return true;
    }
}
