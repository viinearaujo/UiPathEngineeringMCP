using System.Text.Json;
using System.Text.RegularExpressions;

namespace UiPath.Engineering.Mcp.Providers.UiPathCli;

/// <summary>
/// One enabled Workflow Analyzer rule as reported by <c>uip rpa analyzer-rules list</c>.
/// </summary>
public sealed record AnalyzerRule {
    public string Id { get; init; } = string.Empty;
    public string Severity { get; init; } = "info";
    public string Scope { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string? Recommendation { get; init; }
    public string? Docs { get; init; }
    public Dictionary<string, string> Parameters { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Built-in Studio rule (ST-*), package-shipped rule (MA-*), or another owner
    /// (e.g. UI-*/SY-* rules from an installed activity package).
    /// </summary>
    public string Source => Id.StartsWith("ST-", StringComparison.OrdinalIgnoreCase)
        ? "studio-builtin"
        : Id.StartsWith("MA-", StringComparison.OrdinalIgnoreCase)
            ? "package-shipped"
            : "package";
}

/// <summary>
/// Parses the <c>uip rpa analyzer-rules list --output json</c> payload. The envelope carries
/// one pre-rendered text block at <c>Data.message</c>, so rules are read line-based:
/// <c>[severity] ID (Scope) - Title</c> followed by indented <c>recommendation:</c>,
/// <c>parameters:</c> and <c>docs:</c> lines. Scope headings (<c># Workflow</c>) supply the
/// scope for a rule that omits it.
/// </summary>
public static class AnalyzerRulesParser {
    private static readonly Regex RuleLine = new(
        @"^\s*\[(?<severity>error|warning|info|verbose)\]\s+(?<id>[A-Za-z]{2,}(?:-[A-Za-z0-9]+)+)\s*\((?<scope>[^)]*)\)\s*-\s*(?<title>.*?)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ScopeHeading = new(
        @"^\s*#\s*(?<scope>.+?)\s*$",
        RegexOptions.Compiled);

    private static readonly Regex Detail = new(
        @"^\s*(?<name>recommendation|parameters|docs)\s*:\s*(?<value>.*)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Parses the rule list out of the CLI text block (or raw stdout).</summary>
    public static IReadOnlyList<AnalyzerRule> Parse(string? text) {
        if (string.IsNullOrWhiteSpace(text)) {
            return [];
        }

        var builders = new List<RuleBuilder>();
        RuleBuilder? current = null;
        string? headingScope = null;

        foreach (var rawLine in text!.Split(['\r', '\n'])) {
            var line = rawLine.TrimEnd();
            if (line.Trim().Length == 0) {
                continue;
            }

            var rule = RuleLine.Match(line);
            if (rule.Success) {
                current = new RuleBuilder {
                    Id = rule.Groups["id"].Value.Trim(),
                    Severity = NormalizeSeverity(rule.Groups["severity"].Value),
                    Scope = FirstNonEmpty(rule.Groups["scope"].Value.Trim(), headingScope) ?? string.Empty,
                    Title = rule.Groups["title"].Value.Trim()
                };
                builders.Add(current);
                continue;
            }

            var detail = Detail.Match(line);
            if (detail.Success && current is not null) {
                current.Apply(detail.Groups["name"].Value, detail.Groups["value"].Value.Trim());
                continue;
            }

            var heading = ScopeHeading.Match(line);
            if (heading.Success) {
                headingScope = heading.Groups["scope"].Value.Trim();
                current = null;
            }
        }

        return builders.ConvertAll(b => b.ToRule());
    }

    /// <summary>
    /// Reads the rule text out of a parsed CLI response. The payload is a single
    /// <c>Data.message</c> string block; any other string property is accepted as a
    /// fallback so a future structured shape still yields the message text.
    /// </summary>
    public static IReadOnlyList<AnalyzerRule> ParseData(JsonElement? data) {
        if (data is not { } element) {
            return [];
        }

        if (element.ValueKind == JsonValueKind.String) {
            return Parse(element.GetString());
        }

        if (element.ValueKind != JsonValueKind.Object) {
            return [];
        }

        var message = CliEnvelopeParser.GetString(element, "message", "Message", "text", "Text");
        return message is not null ? Parse(message) : [];
    }

    // "InRegex=^in_..., OutRegex=^out_..." — values may contain commas inside regexes,
    // so split only on commas followed by a key= pair.
    private static Dictionary<string, string> ParseParameters(string body) {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(body)) {
            return result;
        }

        foreach (var pair in Regex.Split(body.Trim(), @",\s*(?=[A-Za-z_]\w*=)")) {
            var separator = pair.IndexOf('=');
            if (separator <= 0) {
                continue;
            }

            var key = pair[..separator].Trim();
            var value = pair[(separator + 1)..].Trim();
            if (key.Length > 0) {
                result[key] = value;
            }
        }

        return result;
    }

    private static string NormalizeSeverity(string severity) =>
        severity.Trim().ToLowerInvariant() switch {
            "verbose" => "info",
            var other => other
        };

    private static string? FirstNonEmpty(string? value, string? fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value;

    private sealed class RuleBuilder {
        public required string Id { get; init; }
        public required string Severity { get; init; }
        public required string Scope { get; init; }
        public required string Title { get; init; }
        public string? Recommendation { get; private set; }
        public string? Docs { get; private set; }
        public Dictionary<string, string> Parameters { get; private set; } = new(StringComparer.OrdinalIgnoreCase);

        public void Apply(string name, string value) {
            switch (name.ToLowerInvariant()) {
                case "recommendation":
                    Recommendation = EmptyToNull(value);
                    break;
                case "docs":
                    Docs = EmptyToNull(value);
                    break;
                case "parameters":
                    Parameters = ParseParameters(value);
                    break;
            }
        }

        public AnalyzerRule ToRule() => new() {
            Id = Id,
            Severity = Severity,
            Scope = Scope,
            Title = Title,
            Recommendation = Recommendation,
            Docs = Docs,
            Parameters = Parameters
        };

        private static string? EmptyToNull(string value) =>
            string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
