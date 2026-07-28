using System.Text.RegularExpressions;

namespace UiPath.Engineering.Mcp.Providers.UiPathCli;

/// <summary>
/// Parses uip.exe stdout/stderr into structured error/warning entries.
/// Recognizes analyzer-style lines ("Error  ST-USG-010 : message") and
/// compiler/NuGet-style lines ("error NU1101: message", "warning: message").
/// Unrecognized lines mentioning a severity keyword are preserved verbatim;
/// all non-empty stderr lines are treated as errors so nothing is lost.
/// </summary>
public static class UiPathCliOutputParser
{
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

    public static (List<string> Errors, List<string> Warnings) Parse(string verb, string? stdOut, string? stdErr)
    {
        var errors = new List<string>();
        var warnings = new List<string>();

        foreach (var line in SplitLines(stdOut))
        {
            var analyzer = AnalyzerLine.Match(line);
            if (analyzer.Success)
            {
                Add(analyzer, line, verb, errors, warnings);
                continue;
            }

            var prefixed = SeverityPrefixLine.Match(line);
            if (prefixed.Success)
            {
                Add(prefixed, line, verb, errors, warnings);
                continue;
            }

            var word = SeverityWord.Match(line);
            if (word.Success)
            {
                // Keep the full line so no information is lost.
                Add(word.Groups["severity"].Value, $"[{verb}] {line}", errors, warnings);
            }
        }

        foreach (var line in SplitLines(stdErr))
        {
            errors.Add($"[{verb}] {line}");
        }

        return (errors, warnings);
    }

    private static void Add(Match match, string line, string verb, List<string> errors, List<string> warnings)
    {
        var severity = match.Groups["severity"].Value;
        var code = match.Groups["code"].Value;
        var message = match.Groups["message"].Value.Trim();

        var entry = string.IsNullOrEmpty(code)
            ? $"[{verb}] {message}"
            : $"[{verb}] {code}: {message}";

        Add(severity, entry, errors, warnings);
    }

    private static void Add(string severity, string entry, List<string> errors, List<string> warnings)
    {
        if (severity.StartsWith("warn", StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add(entry);
        }
        else
        {
            errors.Add(entry);
        }
    }

    private static IEnumerable<string> SplitLines(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? []
            : text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                  .Select(l => l.Trim())
                  .Where(l => l.Length > 0);
}
