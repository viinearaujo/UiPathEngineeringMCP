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
            using var doc = ParseDocument(stdOut);
            if (doc is null) {
                return false;
            }
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
                output.EnvelopeRecognized = true;
                if (string.Equals(result.GetString(), "Success", StringComparison.OrdinalIgnoreCase)) {
                    output.EnvelopeSucceeded = true;
                    return true;
                }

                output.EnvelopeSucceeded = false;
                if (!CliDiagnosticExtractor.HasErrorDiagnostic(output)) {
                    AddEnvelopeFailure(verb, root, result, output);
                } else {
                    CliDiagnosticExtractor.TryCollectFromMessage(CliDiagnosticExtractor.GetString(root, "Message"), output);
                }

                return true;
            }

            if (hasSuccess) {
                output.EnvelopeRecognized = true;
                output.EnvelopeSucceeded = success.GetBoolean();
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

            if (output.Diagnostics.Count == 0) {
                return false;
            }

            output.EnvelopeRecognized = true;
            output.EnvelopeSucceeded = !CliDiagnosticExtractor.HasErrorDiagnostic(output);
            return true;
        } catch (JsonException) {
            return false;
        }
    }

    private static JsonDocument? ParseDocument(string stdOut) {
        try {
            return JsonDocument.Parse(stdOut);
        } catch (JsonException) {
            var candidate = CliEnvelopeParser.ExtractJsonObject(stdOut);
            if (candidate is null) {
                return null;
            }

            try {
                return JsonDocument.Parse(candidate);
            } catch (JsonException) {
                return null;
            }
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
