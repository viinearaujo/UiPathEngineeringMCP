using System.Text.Json;
using System.Text.RegularExpressions;
using UiPath.Engineering.Mcp.Core;

namespace UiPath.Engineering.Mcp.Providers.UiPathCli;

/// <summary>
/// Parses uip stdout/stderr into structured error/warning entries.
/// With --output json the CLI emits a response envelope on stdout
/// ({"Result":"Success|...","Message":"...","Instructions":"..."}); that is read first.
/// Otherwise falls back to line-based heuristics: analyzer-style lines
/// ("Error  ST-USG-010 : message") and compiler/NuGet-style lines
/// ("error NU1101: message", "warning: message"). Unrecognized lines mentioning a
/// severity keyword are preserved verbatim; all non-empty stderr lines are treated
/// as errors so nothing is lost.
/// </summary>
public static class UiPathCliOutputParser {
    // Analyzer-style lines, e.g. "Error  ST-USG-010 : Some message".
    private static readonly Regex AnalyzerLine = new(
        @"^\s*(?<severity>Error|Warning)\s+(?<code>[A-Z]{2,}(?:-[A-Z0-9]+)+)\s*:\s*(?<message>.*)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Compiler/NuGet-style lines, e.g. "error NU1101: ...", "warning CS0168: ...", "error: ...".
    private static readonly Regex SeverityPrefixLine = new(
        @"^\s*(?<severity>error|warning)\b(?:\s+(?<code>[A-Z]{1,6}\d+[A-Z0-9]*))?\s*:\s*(?<message>.*)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Fallback: a line mentioning a severity as a standalone word anywhere.
    private static readonly Regex SeverityWord = new(
        @"\b(?<severity>errors?|warnings?)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static (List<string> Errors, List<string> Warnings) Parse(string verb, string? stdOut, string? stdErr) {
        var errors = new List<string>();
        var warnings = new List<string>();

        if (!TryParseJsonEnvelope(verb, stdOut, errors)) {
            foreach (var line in SplitLines(stdOut)) {
                var analyzer = AnalyzerLine.Match(line);
                if (analyzer.Success) {
                    Add(analyzer, line, verb, errors, warnings);
                    continue;
                }

                var prefixed = SeverityPrefixLine.Match(line);
                if (prefixed.Success) {
                    Add(prefixed, line, verb, errors, warnings);
                    continue;
                }

                var word = SeverityWord.Match(line);
                if (word.Success) {
                    // Keep the full line so no information is lost.
                    Add(word.Groups["severity"].Value, $"[{verb}] {line}", errors, warnings);
                }
            }
        }

        foreach (var line in SplitLines(stdErr)) {
            errors.Add(Redact($"[{verb}] {line}"));
        }

        return (errors, warnings);
    }

    // With --output json the CLI answers with a JSON payload instead of line-based
    // diagnostics. Two shapes are recognized (in this order):
    //   1. Response envelope {"Result":"Success|...",...}: "Success" means no errors;
    //      anything else surfaces Message (and Instructions, when present) as one error.
    //   2. Lowercase {"success":false,"errorMessage":"..."} (e.g. rpa validate interop
    //      errors): success=true means no errors; false surfaces errorMessage as one error.
    // Returns true when stdout matched one of the shapes; false falls back to line-based.
    private static bool TryParseJsonEnvelope(string verb, string? stdOut, List<string> errors) {
        if (string.IsNullOrWhiteSpace(stdOut)) {
            return false;
        }

        try {
            using var doc = JsonDocument.Parse(stdOut);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) {
                return false;
            }

            if (root.TryGetProperty("Result", out var result) && result.ValueKind == JsonValueKind.String) {
                if (string.Equals(result.GetString(), "Success", StringComparison.OrdinalIgnoreCase)) {
                    return true;
                }

                var message = GetString(root, "Message");
                var instructions = GetString(root, "Instructions");
                var text = string.Join(" ", new[] { message, instructions }.Where(s => !string.IsNullOrWhiteSpace(s)));
                errors.Add(Redact(text.Length > 0
                    ? $"[{verb}] {text}"
                    : $"[{verb}] command failed with result '{result.GetString()}'."));
                return true;
            }

            if (TryGetPropertyIgnoreCase(root, "success", out var success)
                && success.ValueKind is JsonValueKind.True or JsonValueKind.False) {
                if (success.GetBoolean()) {
                    return true;
                }

                var text = GetStringIgnoreCase(root, "errorMessage")
                    ?? GetStringIgnoreCase(root, "message")
                    ?? stdOut.Trim();
                errors.Add(Redact($"[{verb}] {text}"));
                return true;
            }

            return false;
        } catch (JsonException) {
            return false;
        }
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement root, string name, out JsonElement value) {
        foreach (var property in root.EnumerateObject()) {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string? GetStringIgnoreCase(JsonElement root, string property) =>
        TryGetPropertyIgnoreCase(root, property, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;

    private static string? GetString(JsonElement root, string property) =>
        root.TryGetProperty(property, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;

    private static void Add(Match match, string line, string verb, List<string> errors, List<string> warnings) {
        var severity = match.Groups["severity"].Value;
        var code = match.Groups["code"].Value;
        var message = match.Groups["message"].Value.Trim();

        var entry = string.IsNullOrEmpty(code)
            ? $"[{verb}] {message}"
            : $"[{verb}] {code}: {message}";

        Add(severity, entry, errors, warnings);
    }

    private static void Add(string severity, string entry, List<string> errors, List<string> warnings) {
        // Every parsed entry may echo raw process output (stderr verbatim, JSON envelope
        // messages), so secrets are redacted before anything leaves the parser.
        var redacted = Redact(entry);
        if (severity.StartsWith("warn", StringComparison.OrdinalIgnoreCase)) {
            warnings.Add(redacted);
        } else {
            errors.Add(redacted);
        }
    }

    private static string Redact(string entry) => SecretRedactor.Redact(entry).Text;

    private static IEnumerable<string> SplitLines(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? []
            : text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                  .Select(l => l.Trim())
                  .Where(l => l.Length > 0);
}
