namespace UiPath.Engineering.Mcp.Providers.UiPathCli;

/// <summary>
/// Result of a structured (envelope-parsing) CLI invocation: the standard provider result plus
/// the parsed response envelope. <see cref="Cli"/> carries the capped, redacted stdout for
/// display; the envelope is parsed from the uncapped, redacted stdout so a long run response
/// cannot be silently truncated into unparseable JSON.
/// </summary>
public sealed class UiPathCliRunResult {
    public UiPathCliResult Cli { get; init; } = new();

    /// <summary>Null when the payload could not be parsed as a uip response envelope.</summary>
    public CliEnvelope? Envelope { get; init; }

    private CliRunResult? _verdict;
    private bool _verdictComputed;

    /// <summary>
    /// The run/debug verdict, derived from <see cref="Envelope"/>. Null when the payload was not
    /// a response envelope. Meaningful for <c>rpa run</c> / <c>rpa debug</c>; other verbs read
    /// <see cref="CliEnvelope.Data"/> directly.
    /// </summary>
    public CliRunResult? Verdict {
        get {
            if (_verdictComputed) {
                return _verdict;
            }

            _verdict = Envelope is { } envelope ? CliRunResultParser.FromEnvelope(envelope) : null;
            _verdictComputed = true;
            return _verdict;
        }
    }

    /// <summary>True when execution was refused by the server-side gate before any process ran.</summary>
    public bool Refused { get; init; }

    public bool Parsed => Envelope is not null;

    /// <summary>
    /// The run verdict. When the envelope parsed, that verdict is authoritative — the outer
    /// Result qualifies the CLI invocation, not the workflow. Otherwise the provider's own
    /// success flag is the only signal available.
    /// </summary>
    public bool Succeeded => Verdict is { } verdict ? verdict.Succeeded : Cli.Success;
}

public interface IUiPathCliProvider {
    Task<UiPathCliResult> ValidateAsync(string projectPath, bool validate, bool build, bool pack, CancellationToken cancellationToken = default);

    // Runs an arbitrary CLI invocation, e.g. RunAsync("rpa", "init --name \"X\" ...").
    // 'verb' is used for output parsing/diagnostics; 'arguments' is split into
    // ArgumentList tokens (quoted segments stay one token).
    Task<UiPathCliResult> RunAsync(string verb, string arguments, string? workingDirectory = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs one CLI invocation from pre-built ArgumentList tokens and parses the response
    /// envelope. <paramref name="tokens"/> includes the top-level verb, e.g.
    /// <c>["rpa", "run", "--file-path", "Main.xaml", ...]</c>; <paramref name="verb"/> is the
    /// parsing/diagnostics label for that same verb.
    /// Execution verbs (rpa run / debug / execution) are refused unless
    /// <c>UiPathCli:EnableExecution</c> is set.
    /// </summary>
    /// <remarks>
    /// The default implementation routes through <see cref="RunAsync"/>, so it re-tokenizes and
    /// reads only the capped stdout. <see cref="UiPathCliProvider"/> overrides it with a
    /// token-faithful, uncapped path; the default exists so the interface stays source-compatible
    /// with test doubles.
    /// </remarks>
    async Task<UiPathCliRunResult> RunStructuredAsync(
        string verb,
        IReadOnlyList<string> tokens,
        string? workingDirectory = null,
        int? timeoutSeconds = null,
        CancellationToken cancellationToken = default) {
        var cli = await RunAsync(
            verb, CliVerbArguments.ToArgumentString(tokens), workingDirectory, cancellationToken)
            .ConfigureAwait(false);
        return new UiPathCliRunResult {
            Cli = cli,
            Envelope = CliEnvelopeParser.TryParse(cli.StdOut, out var envelope) ? envelope : null
        };
    }
}
