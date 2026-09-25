using UiPath.Engineering.Mcp.Core.Parsing;

namespace UiPath.Engineering.Mcp.Providers.UiPathCli;

/// <summary>
/// Parses uip stdout/stderr into structured error/warning entries and per-item
/// <see cref="CliDiagnostic"/> rows (file, line, IdRef, property) when the CLI
/// emits them. With --output json the CLI emits a response envelope on stdout
/// ({"Result":"Success|...","Message":"...","Data":...}); Data is walked for
/// diagnostic objects/arrays. Otherwise falls back to line-based heuristics:
/// analyzer-style lines, compiler/NuGet-style lines, and
/// <c>file.xaml(line): error CODE: message</c>. Unrecognized lines mentioning a
/// severity keyword are preserved verbatim; all non-empty stderr lines are treated
/// as errors so nothing is lost.
/// </summary>
public static class UiPathCliOutputParser {
    public static CliParsedOutput Parse(string verb, string? stdOut, string? stdErr) {
        var output = new CliParsedOutput();

        if (!CliOutputEnvelopeParser.TryParseJsonEnvelope(verb, stdOut, output)) {
            foreach (var line in SplitLines(stdOut)) {
                var fileLine = CliDiagnosticExtractor.FileLineDiagnostic.Match(line);
                if (fileLine.Success) {
                    CliDiagnosticExtractor.AddFromFileLineMatch(verb, fileLine, output);
                    continue;
                }

                var analyzer = CliDiagnosticExtractor.AnalyzerLine.Match(line);
                if (analyzer.Success) {
                    CliDiagnosticExtractor.Add(analyzer, line, verb, output);
                    continue;
                }

                var prefixed = CliDiagnosticExtractor.SeverityPrefixLine.Match(line);
                if (prefixed.Success) {
                    CliDiagnosticExtractor.Add(prefixed, line, verb, output);
                    continue;
                }

                var word = CliDiagnosticExtractor.SeverityWord.Match(line);
                if (word.Success) {
                    // Keep the full line so no information is lost.
                    CliDiagnosticExtractor.Add(word.Groups["severity"].Value, $"[{verb}] {line}", output);
                }
            }
        }

        foreach (var line in SplitLines(stdErr)) {
            output.Errors.Add(CliOutputRedactor.Redact($"[{verb}] {line}"));
            var diagnostic = CliDiagnosticExtractor.TryParseDiagnosticLine(line);
            if (diagnostic is not null) {
                output.Diagnostics.Add(CliOutputRedactor.Redact(diagnostic));
            }
        }

        return output;
    }

    internal static IEnumerable<string> SplitLines(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? []
            : text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                  .Select(l => l.Trim())
                  .Where(l => l.Length > 0);
}
