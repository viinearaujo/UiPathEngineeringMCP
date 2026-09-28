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
            || (ExtractJsonObject(text) is { } candidate && TryParseCore(candidate, out envelope));
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

    /// <summary>
    /// The first balanced JSON object in <paramref name="text"/> that looks like a CLI envelope,
    /// or the first balanced object when none of them carry envelope fields. Braces inside
    /// strings are ignored, so a banner such as <c>Updated {cli}</c> is not the payload.
    /// </summary>
    internal static string? ExtractJsonObject(string text) {
        string? fallback = null;
        var index = 0;
        while (index < text.Length) {
            var start = text.IndexOf('{', index);
            if (start < 0) {
                break;
            }

            if (!TryReadBalancedObject(text, start, out var end)) {
                index = start + 1;
                continue;
            }

            var candidate = text[start..(end + 1)];
            index = end + 1;
            try {
                using var document = JsonDocument.Parse(candidate);
                if (document.RootElement.ValueKind != JsonValueKind.Object) {
                    continue;
                }

                if (HasEnvelopeShape(document.RootElement)) {
                    return candidate;
                }

                fallback ??= candidate;
            } catch (JsonException) {
                // Not a JSON object; keep scanning.
            }
        }

        return fallback;
    }

    private static bool HasEnvelopeShape(JsonElement root) {
        foreach (var property in root.EnumerateObject()) {
            if (property.NameEquals("Result") || property.NameEquals("result")
                || property.NameEquals("Data") || property.NameEquals("data")
                || property.NameEquals("success") || property.NameEquals("Success")) {
                return true;
            }
        }

        return false;
    }

    private static bool TryReadBalancedObject(string text, int start, out int end) {
        var depth = 0;
        var inString = false;
        var escape = false;
        for (var i = start; i < text.Length; i++) {
            var c = text[i];
            if (inString) {
                if (escape) {
                    escape = false;
                    continue;
                }

                if (c == '\\') {
                    escape = true;
                    continue;
                }

                if (c == '"') {
                    inString = false;
                }

                continue;
            }

            if (c == '"') {
                inString = true;
                continue;
            }

            if (c == '{') {
                depth++;
            } else if (c == '}') {
                depth--;
                if (depth == 0) {
                    end = i;
                    return true;
                }
            }
        }

        end = -1;
        return false;
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
