using System.Text.Json;
using UiPath.Engineering.Mcp.Core;

namespace UiPath.Engineering.Mcp.Providers.UiPathCli;

/// <summary>One workflow log entry carried on the run/debug response.</summary>
public sealed record CliLogEntry {
    public string? Source { get; init; }
    public string? Level { get; init; }
    public string? Message { get; init; }
}

/// <summary>One execution error carried on the run/debug response.</summary>
public sealed record CliRunError {
    public string? ErrorName { get; init; }
    public string? ErrorMessage { get; init; }
    public int? LineNumber { get; init; }
}

/// <summary>
/// A parsed <c>uip rpa run</c> / <c>uip rpa debug *</c> response.
/// </summary>
public sealed record CliRunResult {
    /// <summary>Envelope verdict. <c>true</c> only when the outer Result is "Success".</summary>
    public bool EnvelopeSuccess { get; init; }
    public string? Result { get; init; }
    public string? Code { get; init; }
    public string? Message { get; init; }
    public string? Instructions { get; init; }

    /// <summary>
    /// Inner verdict from the nested runResult payload. Null when the response used the flat
    /// shape or carried no HasErrors. Never derived from a log entry's Level: a successful
    /// workflow may log at Error level as observability, which is workflow data, not a verdict.
    /// </summary>
    public bool? HasErrors { get; init; }
    public string? ErrorMessage { get; init; }

    /// <summary>Workflow output: serialized output arguments (nested shape) or terminal status (flat shape).</summary>
    public string? Output { get; init; }

    /// <summary>Structured execution errors from the flat response shape.</summary>
    public List<CliRunError> Errors { get; init; } = [];

    /// <summary>Diagnostic context only. Never a verdict source.</summary>
    public List<CliLogEntry> LogEntries { get; init; } = [];
    public bool LogEntriesTruncated { get; init; }

    /// <summary>
    /// Debug sessions only; null on a plain run. Check this BEFORE HasErrors:
    /// a Suspended session has an awaiting exception while HasErrors is still false.
    /// </summary>
    public string? DebugState { get; init; }
    public JsonElement? DebugDetails { get; init; }

    /// <summary>Absolute directory holding the run's *.uistat profiling files and screenshots.</summary>
    public string? ProfilingOutputDirectory { get; init; }

    /// <summary>True when the response used the nested <c>Data.runResult</c> JSON-string shape.</summary>
    public bool UsedRunResultEnvelope { get; init; }

    /// <summary>True when the session reached a terminal state.</summary>
    public bool IsCompleted => string.Equals(DebugState, "Completed", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when execution paused at a breakpoint; locals are in <see cref="DebugDetails"/>.</summary>
    public bool IsPaused => string.Equals(DebugState, "Paused", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when an exception awaits a continue / retry / ignore decision.</summary>
    public bool IsSuspended => string.Equals(DebugState, "Suspended", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the session is still moving and no outcome is decided yet.</summary>
    public bool IsRunning => string.Equals(DebugState, "Running", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when the session is alive and the outcome is undecided — check this before
    /// <see cref="Succeeded"/>, since a Suspended session reports HasErrors false.
    /// </summary>
    public bool IsAwaitingDecision => IsSuspended || IsPaused || IsRunning;

    /// <summary>
    /// The single verdict for a completed run. The envelope Result qualifies the CLI invocation
    /// (it stays "Success" once the runtime was invoked), so it is cross-checked against the
    /// workflow-level signals and never against a log entry's Level:
    /// <list type="bullet">
    /// <item>HasErrors, when the response carried it (nested runResult, or a flat payload that
    /// still reports it) — authoritative.</item>
    /// <item>DebugState before HasErrors: a Suspended session has an awaiting exception while
    /// HasErrors is still false, so an undecided session is never a pass.</item>
    /// <item>Data.errors (flat shape) — populated for exceptions and compile failures.</item>
    /// <item>Data.output (plain run, flat shape) — a missing entry point leaves errors empty and
    /// reports the failure here, so a non-empty terminal status other than "Session ended" is not
    /// a pass.</item>
    /// </list>
    /// </summary>
    public bool Succeeded {
        get {
            if (!EnvelopeSuccess) {
                return false;
            }

            if (IsAwaitingDecision) {
                return false;
            }

            if (HasErrors is { } hasErrors) {
                return !hasErrors;
            }

            if (Errors.Count > 0) {
                return false;
            }

            // Flat shape with no HasErrors: output is the terminal status. Mid-session debug
            // commands report an empty output, so only a populated non-success status fails.
            return string.IsNullOrWhiteSpace(Output)
                || string.Equals(Output.Trim(), FlatShapeSuccessOutput, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>The flat shape's clean-completion terminal status.</summary>
    public const string FlatShapeSuccessOutput = "Session ended";

    /// <summary>DebugState value meaning no session exists.</summary>
    public const string DebugStateNone = "None";
}

/// <summary>
/// Reads the <c>uip rpa run</c> / <c>uip rpa debug</c> response. Two payload shapes exist and
/// are both handled by key presence, never by assumed schema: the nested
/// <c>{Result, Code, Data:{runResult:"&lt;json-string&gt;"}}</c> form, where <c>runResult</c> is a JSON
/// STRING parsed separately and carries <c>HasErrors</c>/<c>DebugState</c>/<c>Profiling</c>; and the
/// flat <c>Data:{output, errors, logEntries}</c> form. The verdict comes from those fields and the
/// envelope Result only — a streamed log entry's Level is never a failure signal.
/// </summary>
public static class CliRunResultParser {
    private const int MaxLogEntries = 200;

    public static CliRunResult? Parse(string? stdOut) {
        if (!CliEnvelopeParser.TryParse(stdOut, out var envelope)) {
            return null;
        }

        return FromEnvelope(envelope);
    }

    public static CliRunResult FromEnvelope(CliEnvelope envelope) {
        var result = new CliRunResult {
            EnvelopeSuccess = envelope.IsSuccess,
            Result = envelope.Result,
            Code = envelope.Code,
            Message = envelope.Message,
            Instructions = envelope.Instructions
        };

        if (envelope.Data is not { } data) {
            return result;
        }

        // The nested shape carries a JSON-encoded string that must be parsed separately.
        var usedRunResult = false;
        if (CliEnvelopeParser.TryGetProperty(data, "runResult", out var runResult)
            && TryReadRunResult(runResult, out var nested)) {
            data = nested;
            usedRunResult = true;
        }

        var (logEntries, truncated) = ReadLogEntries(data);

        return new CliRunResult {
            EnvelopeSuccess = result.EnvelopeSuccess,
            Result = result.Result,
            Code = result.Code,
            Message = Redact(result.Message),
            Instructions = Redact(result.Instructions),
            UsedRunResultEnvelope = usedRunResult,
            HasErrors = CliEnvelopeParser.GetBool(data, "hasErrors", "HasErrors"),
            ErrorMessage = Redact(CliEnvelopeParser.GetString(data, "errorMessage", "ErrorMessage")),
            Output = Redact(CliEnvelopeParser.GetString(data, "output", "Output")),
            Errors = ReadErrors(data),
            LogEntries = logEntries,
            LogEntriesTruncated = truncated,
            DebugState = CliEnvelopeParser.GetString(data, "debugState", "DebugState"),
            DebugDetails = GetDebugDetails(data),
            ProfilingOutputDirectory = ReadProfilingOutputDirectory(data)
        };
    }

    // The envelope itself is parsed un-redacted (redacting raw JSON would corrupt it and lose the
    // verdict), so each string that can carry process output is redacted here instead. A workflow
    // that logs a credential must not leak it through errorMessage, output, or a log entry.
    private static string? Redact(string? text) =>
        string.IsNullOrEmpty(text) ? text : SecretRedactor.Redact(text).Text;

    private static bool TryReadRunResult(JsonElement runResult, out JsonElement parsed) {
        parsed = default;

        switch (runResult.ValueKind) {
            case JsonValueKind.String:
                var text = runResult.GetString();
                if (string.IsNullOrWhiteSpace(text)) {
                    return false;
                }

                try {
                    using var document = JsonDocument.Parse(text!);
                    if (document.RootElement.ValueKind != JsonValueKind.Object) {
                        return false;
                    }

                    parsed = document.RootElement.Clone();
                    return true;
                } catch (JsonException) {
                    return false;
                }

            case JsonValueKind.Object:
                parsed = runResult.Clone();
                return true;

            default:
                return false;
        }
    }

    private static List<CliRunError> ReadErrors(JsonElement data) {
        var errors = new List<CliRunError>();
        if (!CliEnvelopeParser.TryGetProperty(data, "errors", out var array)
            && !CliEnvelopeParser.TryGetProperty(data, "Errors", out array)) {
            return errors;
        }

        if (array.ValueKind != JsonValueKind.Array) {
            return errors;
        }

        foreach (var item in array.EnumerateArray()) {
            errors.Add(item.ValueKind switch {
                JsonValueKind.String => new CliRunError { ErrorMessage = Redact(item.GetString()) },
                JsonValueKind.Object => new CliRunError {
                    ErrorName = CliEnvelopeParser.GetString(item, "errorName", "ErrorName", "name", "Name"),
                    ErrorMessage = Redact(CliEnvelopeParser.GetString(item, "errorMessage", "ErrorMessage", "message", "Message")),
                    LineNumber = ReadInt(item, "lineNumber", "LineNumber", "line", "Line")
                },
                _ => new CliRunError()
            });
        }

        return errors;
    }

    private static (List<CliLogEntry> Entries, bool Truncated) ReadLogEntries(JsonElement data) {
        var entries = new List<CliLogEntry>();
        if (!CliEnvelopeParser.TryGetProperty(data, "logEntries", out var array)
            && !CliEnvelopeParser.TryGetProperty(data, "LogEntries", out array)) {
            return (entries, false);
        }

        if (array.ValueKind != JsonValueKind.Array) {
            return (entries, false);
        }

        var total = 0;
        foreach (var item in array.EnumerateArray()) {
            total++;
            if (entries.Count >= MaxLogEntries) {
                continue;
            }

            entries.Add(new CliLogEntry {
                Source = CliEnvelopeParser.GetString(item, "source", "Source"),
                Level = CliEnvelopeParser.GetString(item, "level", "Level"),
                Message = Redact(CliEnvelopeParser.GetString(item, "message", "Message"))
            });
        }

        return (entries, total > entries.Count);
    }

    private static JsonElement? GetDebugDetails(JsonElement data) {
        foreach (var name in new[] { "debugDetails", "DebugDetails" }) {
            if (!CliEnvelopeParser.TryGetProperty(data, name, out var value)
                || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) {
                continue;
            }

            // The documented shape is a JSON-encoded string snapshot; return the parsed
            // element when possible so callers see an object rather than escaped text.
            if (value.ValueKind == JsonValueKind.String) {
                var text = value.GetString();
                if (!string.IsNullOrWhiteSpace(text)) {
                    try {
                        using var document = JsonDocument.Parse(text!);
                        return document.RootElement.Clone();
                    } catch (JsonException) {
                        return value.Clone();
                    }
                }

                return null;
            }

            return value.Clone();
        }

        return null;
    }

    private static int? ReadInt(JsonElement element, params string[] names) {
        foreach (var name in names) {
            if (!CliEnvelopeParser.TryGetProperty(element, name, out var value)) {
                continue;
            }

            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n)) {
                return n;
            }

            if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out var parsed)) {
                return parsed;
            }
        }

        return null;
    }

    private static string? ReadProfilingOutputDirectory(JsonElement data) {
        if (!CliEnvelopeParser.TryGetProperty(data, "profiling", out var profiling)
            && !CliEnvelopeParser.TryGetProperty(data, "Profiling", out profiling)) {
            return null;
        }

        if (profiling.ValueKind != JsonValueKind.Object) {
            return null;
        }

        return CliEnvelopeParser.GetString(profiling, "outputDirectory", "OutputDirectory");
    }
}
