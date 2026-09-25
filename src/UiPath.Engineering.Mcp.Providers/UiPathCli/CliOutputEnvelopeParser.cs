using System.Text.Json;

namespace UiPath.Engineering.Mcp.Providers.UiPathCli;

/// <summary>
/// Parses the uip JSON response envelope shapes embedded in tool stdout
/// (Result/Message/Data and success/errorMessage) for <see cref="UiPathCliOutputParser"/>.
/// </summary>
public static class CliOutputEnvelopeParser {
    internal static bool TryParseJsonEnvelope(string verb, string? stdOut, CliParsedOutput output) {
        if (string.IsNullOrWhiteSpace(stdOut)) {
            return false;
        }

        try {
            using var doc = JsonDocument.Parse(stdOut);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) {
                return false;
            }

            var hasResult = root.TryGetProperty("Result", out var result) && result.ValueKind == JsonValueKind.String;
            var hasSuccess = CliDiagnosticExtractor.TryGetPropertyIgnoreCase(root, "success", out var success)
                && success.ValueKind is JsonValueKind.True or JsonValueKind.False;
            var hasData = CliDiagnosticExtractor.TryGetPropertyIgnoreCase(root, "Data", out var data)
                && data.ValueKind is JsonValueKind.Object or JsonValueKind.Array;

            if (!hasResult && !hasSuccess && !hasData) {
                return false;
            }

            if (hasData) {
                CliDiagnosticExtractor.CollectDiagnostics(verb, data, output);
            }

            if (hasResult) {
                if (string.Equals(result.GetString(), "Success", StringComparison.OrdinalIgnoreCase)) {
                    return true;
                }

                if (!CliDiagnosticExtractor.HasErrorDiagnostic(output)) {
                    AddEnvelopeFailure(verb, root, result, output);
                } else {
                    CliDiagnosticExtractor.TryCollectFromMessage(CliDiagnosticExtractor.GetString(root, "Message"), output);
                }

                return true;
            }

            if (hasSuccess) {
                if (success.GetBoolean()) {
                    return true;
                }

                if (!CliDiagnosticExtractor.HasErrorDiagnostic(output)) {
                    var text = CliDiagnosticExtractor.GetStringIgnoreCase(root, "errorMessage")
                        ?? CliDiagnosticExtractor.GetStringIgnoreCase(root, "message")
                        ?? stdOut.Trim();
                    output.Errors.Add(CliOutputRedactor.Redact($"[{verb}] {text}"));
                }

                return true;
            }

            return output.Diagnostics.Count > 0;
        } catch (JsonException) {
            return false;
        }
    }

    private static void AddEnvelopeFailure(string verb, JsonElement root, JsonElement result, CliParsedOutput output) {
        var message = CliDiagnosticExtractor.GetString(root, "Message");
        var instructions = CliDiagnosticExtractor.GetString(root, "Instructions");
        var text = string.Join(" ", new[] { message, instructions }.Where(s => !string.IsNullOrWhiteSpace(s)));
        output.Errors.Add(CliOutputRedactor.Redact(text.Length > 0
            ? $"[{verb}] {text}"
            : $"[{verb}] command failed with result '{result.GetString()}'."));
        CliDiagnosticExtractor.TryCollectFromMessage(message, output);
    }
}
