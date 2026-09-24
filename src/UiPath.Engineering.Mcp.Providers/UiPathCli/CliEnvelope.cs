using System.Text.Json;

namespace UiPath.Engineering.Mcp.Providers.UiPathCli;

/// <summary>
/// The shared uip response envelope: <c>{Result, Code, Data, Message?, Instructions?}</c>.
/// <see cref="Data"/> is a detached clone, so it stays valid after parsing returns.
/// </summary>
public sealed record CliEnvelope(
    string? Result,
    string? Code,
    string? Message,
    string? Instructions,
    JsonElement? Data) {
    /// <summary>
    /// The envelope-level verdict. Callers that read a run/debug result must treat this
    /// (and the inner HasErrors) as the ONLY source of truth — never a streamed log level.
    /// </summary>
    public bool IsSuccess => string.Equals(Result, "Success", StringComparison.OrdinalIgnoreCase);
}

public static class CliEnvelopeParser {
    // The CLI can print banner/update text on stdout ahead of the payload, so a bare
    // JsonDocument.Parse is not enough; fall back to the outermost brace pair.
    public static bool TryParse(string? stdOut, out CliEnvelope envelope) {
        envelope = new CliEnvelope(null, null, null, null, null);
        if (string.IsNullOrWhiteSpace(stdOut)) {
            return false;
        }

        // A JSON array root is never a response envelope, and the brace-slicing fallback below
        // would otherwise mis-read one array item as the payload.
        if (stdOut!.TrimStart().StartsWith('[')) {
            return false;
        }

        var text = stdOut!;
        return TryParseCore(text, out envelope)
            || (ExtractJsonCandidate(text) is { } candidate && TryParseCore(candidate, out envelope));
    }

    private static bool TryParseCore(string text, out CliEnvelope envelope) {
        envelope = new CliEnvelope(null, null, null, null, null);
        try {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) {
                return false;
            }

            envelope = new CliEnvelope(
                GetString(root, "Result"),
                GetString(root, "Code"),
                GetString(root, "Message"),
                GetString(root, "Instructions"),
                GetData(root));
            return true;
        } catch (JsonException) {
            return false;
        }
    }

    private static JsonElement? GetData(JsonElement root) {
        foreach (var property in root.EnumerateObject()) {
            if (string.Equals(property.Name, "Data", StringComparison.OrdinalIgnoreCase)) {
                // Clone so the element outlives the JsonDocument.
                return property.Value.Clone();
            }
        }

        return null;
    }

    private static string? GetString(JsonElement root, string name) {
        foreach (var property in root.EnumerateObject()) {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)
                && property.Value.ValueKind == JsonValueKind.String) {
                return property.Value.GetString();
            }
        }

        return null;
    }

    private static string? ExtractJsonCandidate(string text) {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        return start >= 0 && end > start ? text[start..(end + 1)] : null;
    }

    /// <summary>Case-insensitive property lookup on a parsed element.</summary>
    public static bool TryGetProperty(JsonElement element, string name, out JsonElement value) {
        value = default;
        if (element.ValueKind != JsonValueKind.Object) {
            return false;
        }

        foreach (var property in element.EnumerateObject()) {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) {
                value = property.Value;
                return true;
            }
        }

        return false;
    }

    public static string? GetString(JsonElement element, params string[] names) {
        foreach (var name in names) {
            if (TryGetProperty(element, name, out var value) && value.ValueKind == JsonValueKind.String) {
                var text = value.GetString();
                if (!string.IsNullOrWhiteSpace(text)) {
                    return text;
                }
            }
        }

        return null;
    }

    public static bool? GetBool(JsonElement element, params string[] names) {
        foreach (var name in names) {
            if (!TryGetProperty(element, name, out var value)) {
                continue;
            }

            if (value.ValueKind is JsonValueKind.True or JsonValueKind.False) {
                return value.GetBoolean();
            }

            if (value.ValueKind == JsonValueKind.String
                && bool.TryParse(value.GetString(), out var parsed)) {
                return parsed;
            }
        }

        return null;
    }
}
