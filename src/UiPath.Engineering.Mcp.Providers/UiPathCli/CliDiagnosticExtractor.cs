using System.Text.Json;
using System.Text.RegularExpressions;
using UiPath.Engineering.Mcp.Core.Parsing;

namespace UiPath.Engineering.Mcp.Providers.UiPathCli;

/// <summary>
/// Extracts structured <see cref="CliDiagnostic"/> rows from JSON Data payloads
/// and from analyzer / compiler line formats.
/// </summary>
public static class CliDiagnosticExtractor {
    // Analyzer-style lines, e.g. "Error  ST-USG-010 : Some message".
    internal static readonly Regex AnalyzerLine = new(
        @"^\s*(?<severity>Error|Warning)\s+(?<code>[A-Z]{2,}(?:-[A-Z0-9]+)+)\s*:\s*(?<message>.*)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Compiler/NuGet-style lines, e.g. "error NU1101: ...", "warning CS0168: ...", "error: ...".
    internal static readonly Regex SeverityPrefixLine = new(
        @"^\s*(?<severity>error|warning)\b(?:\s+(?<code>[A-Z]{1,6}\d+[A-Z0-9]*))?\s*:\s*(?<message>.*)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Fallback: a line mentioning a severity as a standalone word anywhere.
    internal static readonly Regex SeverityWord = new(
        @"\b(?<severity>errors?|warnings?)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Compiler location: "Main.xaml(12,5): error BC30451: 'foo' is not declared."
    internal static readonly Regex FileLineDiagnostic = new(
        @"^\s*(?<file>(?:[a-zA-Z]:)?[^\r\n:]+\.xaml)\((?<line>\d+)(?:,\d+)?\)\s*:\s*(?<severity>error|warning)\s+(?<code>[A-Z]{1,6}\d+[A-Z0-9]*)?\s*:?\s*(?<message>.*)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    internal static readonly Regex PropertyInMessage = new(
        @"(?:property|member|argument)\s+['""]([^'""]+)['""]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    internal static readonly Regex IdRefInMessage = new(
        @"\b(?:ActivityIdRef|IdRef)\s*[=:]\s*['""]?([A-Za-z_][\w`]*)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    internal static readonly string[] DiagnosticArrayNames = [
        "Errors",
        "Warnings",
        "Diagnostics",
        "Issues",
        "Items",
        "Results",
        "Violations",
        "Messages"
    ];

    internal static void TryCollectFromMessage(string? message, CliParsedOutput output) {
        if (string.IsNullOrWhiteSpace(message)) {
            return;
        }

        foreach (var line in UiPathCliOutputParser.SplitLines(message)) {
            var diagnostic = TryParseDiagnosticLine(line);
            if (diagnostic is not null && !AlreadyHave(output, diagnostic)) {
                output.Diagnostics.Add(CliOutputRedactor.Redact(diagnostic));
            }
        }
    }

    internal static void CollectDiagnostics(string verb, JsonElement data, CliParsedOutput output) {
        if (data.ValueKind == JsonValueKind.Array) {
            foreach (var item in data.EnumerateArray()) {
                AddDataItem(verb, item, output, defaultSeverity: "error");
            }

            return;
        }

        if (data.ValueKind != JsonValueKind.Object) {
            return;
        }

        var foundNamedArray = false;
        foreach (var name in DiagnosticArrayNames) {
            if (!TryGetPropertyIgnoreCase(data, name, out var array) || array.ValueKind != JsonValueKind.Array) {
                continue;
            }

            foundNamedArray = true;
            var severity = name.Equals("Warnings", StringComparison.OrdinalIgnoreCase) ? "warning" : "error";
            foreach (var item in array.EnumerateArray()) {
                AddDataItem(verb, item, output, severity);
            }
        }

        if (foundNamedArray) {
            return;
        }

        // Analyzer-style dictionary: { "Main.xaml": [ { ... }, ... ] } or a single diagnostic object.
        foreach (var property in data.EnumerateObject()) {
            if (property.Value.ValueKind == JsonValueKind.Array) {
                foreach (var item in property.Value.EnumerateArray()) {
                    AddDataItem(verb, item, output, defaultSeverity: "error", fallbackFile: property.Name);
                }
            } else if (LooksLikeDiagnostic(property.Value)) {
                AddDataItem(verb, property.Value, output, defaultSeverity: "error");
            }
        }
    }

    internal static bool LooksLikeDiagnostic(JsonElement element) =>
        element.ValueKind == JsonValueKind.Object
        && (HasAnyString(element, "Message", "Description", "ErrorMessage", "FilePath", "File", "ErrorCode", "ActivityIdRef", "IdRef")
            || HasNumber(element, "Line", "LineNumber"));

    internal static void AddDataItem(
        string verb, JsonElement item, CliParsedOutput output, string defaultSeverity, string? fallbackFile = null) {
        if (item.ValueKind == JsonValueKind.String) {
            var text = item.GetString();
            if (string.IsNullOrWhiteSpace(text)) {
                return;
            }

            var fromLine = TryParseDiagnosticLine(text);
            if (fromLine is not null) {
                AddDiagnostic(verb, fromLine, output);
                return;
            }

            AddDiagnostic(verb, new CliDiagnostic {
                Message = text.Trim(),
                FilePath = fallbackFile is not null && fallbackFile.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)
                    ? fallbackFile
                    : null,
                Severity = defaultSeverity
            }, output);
            return;
        }

        var diagnostic = TryReadDiagnosticObject(item, defaultSeverity, fallbackFile);
        if (diagnostic is not null) {
            AddDiagnostic(verb, diagnostic, output);
        }
    }

    internal static CliDiagnostic? TryReadDiagnosticObject(
        JsonElement item, string defaultSeverity, string? fallbackFile) {
        if (item.ValueKind != JsonValueKind.Object) {
            return null;
        }

        JsonElement location = default;
        var hasLocation = TryGetPropertyIgnoreCase(item, "Location", out location)
            && location.ValueKind == JsonValueKind.Object;
        var loc = hasLocation ? location : item;

        var file = FirstWorkflowPath(item)
            ?? FirstWorkflowPath(loc)
            ?? (IsWorkflowPath(fallbackFile) ? fallbackFile : null);
        var line = FirstInt(item, "Line", "LineNumber") ?? FirstInt(loc, "Line", "LineNumber");
        var idRef = FirstString(item, "ActivityIdRef", "IdRef", "ActivityId", "ActivityID")
            ?? FirstString(loc, "ActivityIdRef", "IdRef", "ActivityId");
        var displayName = FirstString(item, "ActivityDisplayName", "DisplayName", "ActivityName");
        var property = FirstString(item, "Property", "PropertyName", "Member", "SourceMember");
        var message = FirstString(item, "Message", "Description", "ErrorMessage", "Text") ?? "";
        var recommendation = FirstString(item, "Recommendation", "SuggestedFix", "RecommendationMessage", "Instructions");
        var code = FirstString(item, "ErrorCode", "Code", "RuleId");
        var severity = ReadSeverity(item) ?? defaultSeverity;

        if (TryGetPropertyIgnoreCase(item, "Item", out var nestedItem)) {
            if (nestedItem.ValueKind == JsonValueKind.Object) {
                property ??= FirstString(nestedItem, "Name", "Property", "Member");
                displayName ??= FirstString(nestedItem, "DisplayName");
            } else if (nestedItem.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(property)) {
                property = nestedItem.GetString();
            }
        }

        if (string.IsNullOrWhiteSpace(property) && !string.IsNullOrWhiteSpace(message)) {
            property = MatchGroup(PropertyInMessage, message);
        }

        if (string.IsNullOrWhiteSpace(idRef) && !string.IsNullOrWhiteSpace(message)) {
            idRef = MatchGroup(IdRefInMessage, message);
        }

        if (string.Equals(severity, "info", StringComparison.OrdinalIgnoreCase)
            || string.Equals(severity, "verbose", StringComparison.OrdinalIgnoreCase)) {
            return null;
        }

        var hasRealMessage = !string.IsNullOrWhiteSpace(FirstString(item, "Message", "Description", "ErrorMessage", "Text"));
        if (!hasRealMessage && !IsWorkflowPath(file) && string.IsNullOrWhiteSpace(idRef)
            && string.IsNullOrWhiteSpace(property) && line is null && string.IsNullOrWhiteSpace(code)) {
            return null;
        }

        if (string.IsNullOrWhiteSpace(message) && string.IsNullOrWhiteSpace(file)
            && string.IsNullOrWhiteSpace(idRef) && string.IsNullOrWhiteSpace(property)) {
            return null;
        }

        if (string.IsNullOrWhiteSpace(message)) {
            message = code is not null ? $"{code} validation issue." : "Validation issue.";
        }

        return new CliDiagnostic {
            Message = message.Trim(),
            FilePath = string.IsNullOrWhiteSpace(file) ? null : file.Trim(),
            Line = line,
            IdRef = string.IsNullOrWhiteSpace(idRef) ? null : idRef.Trim(),
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim(),
            Property = string.IsNullOrWhiteSpace(property) ? null : property.Trim(),
            Recommendation = string.IsNullOrWhiteSpace(recommendation) ? null : recommendation.Trim(),
            Code = string.IsNullOrWhiteSpace(code) ? null : code.Trim(),
            Severity = severity
        };
    }

    internal static string? ReadSeverity(JsonElement item) {
        if (TryGetPropertyIgnoreCase(item, "ErrorSeverity", out var element)
            || TryGetPropertyIgnoreCase(item, "Severity", out element)
            || TryGetPropertyIgnoreCase(item, "ErrorType", out element)
            || TryGetPropertyIgnoreCase(item, "Level", out element)) {
            if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var n)) {
                return n switch {
                    1 => "error",
                    2 => "warning",
                    >= 3 => "info",
                    _ => "error"
                };
            }

            if (element.ValueKind == JsonValueKind.String) {
                var text = element.GetString() ?? "";
                if (text.StartsWith("warn", StringComparison.OrdinalIgnoreCase)) {
                    return "warning";
                }

                if (text.StartsWith("info", StringComparison.OrdinalIgnoreCase)
                    || text.StartsWith("verbose", StringComparison.OrdinalIgnoreCase)) {
                    return "info";
                }

                if (text.StartsWith("error", StringComparison.OrdinalIgnoreCase)) {
                    return "error";
                }
            }
        }

        return null;
    }

    internal static void AddFromFileLineMatch(string verb, Match match, CliParsedOutput output) {
        var diagnostic = DiagnosticFromFileLineMatch(match);
        AddDiagnostic(verb, diagnostic, output);
    }

    internal static CliDiagnostic? TryParseDiagnosticLine(string line) {
        var match = FileLineDiagnostic.Match(line);
        return match.Success ? DiagnosticFromFileLineMatch(match) : null;
    }

    internal static CliDiagnostic DiagnosticFromFileLineMatch(Match match) {
        var message = match.Groups["message"].Value.Trim();
        var file = match.Groups["file"].Value.Trim();
        int? line = int.TryParse(match.Groups["line"].Value, out var n) ? n : null;
        var code = match.Groups["code"].Success && match.Groups["code"].Value.Length > 0
            ? match.Groups["code"].Value
            : null;
        return new CliDiagnostic {
            Message = message.Length > 0 ? message : match.Value.Trim(),
            FilePath = file,
            Line = line,
            Property = MatchGroup(PropertyInMessage, message),
            IdRef = MatchGroup(IdRefInMessage, message),
            Code = code,
            Severity = match.Groups["severity"].Value.StartsWith("warn", StringComparison.OrdinalIgnoreCase)
                ? "warning"
                : "error"
        };
    }

    internal static void AddDiagnostic(string verb, CliDiagnostic diagnostic, CliParsedOutput output) {
        var redacted = CliOutputRedactor.Redact(diagnostic);
        if (AlreadyHave(output, redacted)) {
            return;
        }

        output.Diagnostics.Add(redacted);
        var entry = FormatEntry(verb, redacted);
        if (IsWarning(redacted.Severity)) {
            output.Warnings.Add(entry);
        } else {
            output.Errors.Add(entry);
        }
    }

    internal static string FormatEntry(string verb, CliDiagnostic diagnostic) {
        var file = diagnostic.FilePath is not null ? Path.GetFileName(diagnostic.FilePath) : null;
        var location = file is not null
            ? diagnostic.Line is int line ? $"{file}({line}): " : $"{file}: "
            : "";
        var code = diagnostic.Code is not null ? $"{diagnostic.Code}: " : "";
        return CliOutputRedactor.Redact($"[{verb}] {location}{code}{diagnostic.Message}".Trim());
    }

    internal static bool HasErrorDiagnostic(CliParsedOutput output) =>
        output.Diagnostics.Any(d => !IsWarning(d.Severity));

    internal static bool AlreadyHave(CliParsedOutput output, CliDiagnostic diagnostic) =>
        output.Diagnostics.Any(d =>
            string.Equals(d.Message, diagnostic.Message, StringComparison.Ordinal)
            && string.Equals(d.FilePath, diagnostic.FilePath, StringComparison.OrdinalIgnoreCase)
            && d.Line == diagnostic.Line
            && string.Equals(d.IdRef, diagnostic.IdRef, StringComparison.OrdinalIgnoreCase)
            && string.Equals(d.Property, diagnostic.Property, StringComparison.OrdinalIgnoreCase));

    internal static bool IsWarning(string? severity) =>
        severity is not null && severity.StartsWith("warn", StringComparison.OrdinalIgnoreCase);

    internal static bool TryGetPropertyIgnoreCase(JsonElement root, string name, out JsonElement value) {
        foreach (var property in root.EnumerateObject()) {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    internal static string? GetStringIgnoreCase(JsonElement root, string property) =>
        TryGetPropertyIgnoreCase(root, property, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;

    internal static string? GetString(JsonElement root, string property) =>
        root.TryGetProperty(property, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;

    internal static string? FirstWorkflowPath(JsonElement root) {
        var preferred = FirstString(root, "FilePath", "File", "WorkflowFile", "RelativeFilePath");
        if (!string.IsNullOrWhiteSpace(preferred)) {
            return preferred;
        }

        var path = FirstString(root, "Path");
        return IsWorkflowPath(path) ? path : null;
    }

    internal static bool IsWorkflowPath(string? path) =>
        path is not null
        && (path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase));

    internal static string? FirstString(JsonElement root, params string[] names) {
        foreach (var name in names) {
            var value = GetStringIgnoreCase(root, name);
            if (!string.IsNullOrWhiteSpace(value)) {
                return value;
            }
        }

        return null;
    }

    internal static int? FirstInt(JsonElement root, params string[] names) {
        foreach (var name in names) {
            if (!TryGetPropertyIgnoreCase(root, name, out var element)) {
                continue;
            }

            if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var n)) {
                return n;
            }

            if (element.ValueKind == JsonValueKind.String && int.TryParse(element.GetString(), out var parsed)) {
                return parsed;
            }
        }

        return null;
    }

    internal static bool HasAnyString(JsonElement root, params string[] names) =>
        names.Any(n => !string.IsNullOrWhiteSpace(GetStringIgnoreCase(root, n)));

    internal static bool HasNumber(JsonElement root, params string[] names) =>
        FirstInt(root, names) is not null;

    internal static string? MatchGroup(Regex regex, string text) {
        var match = regex.Match(text);
        return match.Success ? match.Groups[1].Value : null;
    }

    internal static void Add(Match match, string line, string verb, CliParsedOutput output) {
        var severity = match.Groups["severity"].Value;
        var code = match.Groups["code"].Value;
        var message = match.Groups["message"].Value.Trim();

        var entry = string.IsNullOrEmpty(code)
            ? $"[{verb}] {message}"
            : $"[{verb}] {code}: {message}";

        Add(severity, entry, output);

        var diagnostic = TryParseDiagnosticLine(message) ?? TryParseDiagnosticLine(line);
        if (diagnostic is not null) {
            if (string.IsNullOrWhiteSpace(diagnostic.Code) && !string.IsNullOrEmpty(code)) {
                diagnostic = new CliDiagnostic {
                    Message = diagnostic.Message,
                    FilePath = diagnostic.FilePath,
                    Line = diagnostic.Line,
                    IdRef = diagnostic.IdRef,
                    DisplayName = diagnostic.DisplayName,
                    Property = diagnostic.Property ?? MatchGroup(PropertyInMessage, message),
                    Recommendation = diagnostic.Recommendation,
                    Code = code,
                    Severity = IsWarning(severity) ? "warning" : diagnostic.Severity
                };
            }

            if (!AlreadyHave(output, diagnostic)) {
                output.Diagnostics.Add(CliOutputRedactor.Redact(diagnostic));
            }
        }
    }

    internal static void Add(string severity, string entry, CliParsedOutput output) {
        // Every parsed entry may echo raw process output (stderr verbatim, JSON envelope
        // messages), so secrets are redacted before anything leaves the parser.
        var redacted = CliOutputRedactor.Redact(entry);
        if (severity.StartsWith("warn", StringComparison.OrdinalIgnoreCase)) {
            output.Warnings.Add(redacted);
        } else {
            output.Errors.Add(redacted);
        }
    }
}
