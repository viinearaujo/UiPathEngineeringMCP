using UiPath.Engineering.Mcp.Core.Configuration;

namespace UiPath.Engineering.Mcp.Providers.UiPathCli;

public enum CliCommandClass { AllowedReadOnly, AllowedMutating, VerbNotAllowed, ArgumentsRejected }

// Decides whether a `uip <verb> <args>` invocation may run, based on
// UiPathCliOptions. Fails closed: unknown subcommands are treated as mutating.
// Injection control is ProcessStartInfo.ArgumentList (each token is one
// argument). Newlines and NUL cannot be represented as a single token through
// the string API and are rejected so they never reach the tokenizer.
public sealed class CliCommandPolicy {
    private static readonly char[] RejectedControlChars = ['\r', '\n', '\0'];

    // Subcommands that execute automation on this machine. Gated by
    // UiPathCliOptions.EnableExecution, a separate and stricter switch than
    // EnableMutatingCommands. These are deliberately NOT in ReadOnlySubcommands,
    // so Classify still reports them as mutating and the run_ui_path_cli hatch
    // cannot reach them without EnableMutatingCommands either.
    private static readonly Dictionary<string, string[]> ExecutionSubcommands = new(StringComparer.OrdinalIgnoreCase) {
        ["rpa"] = ["run", "debug", "execution"]
    };

    private readonly UiPathCliOptions _options;

    public CliCommandPolicy(UiPathCliOptions options) {
        _options = options;
    }

    public CliCommandClass Classify(string verb, string arguments) {
        if (!_options.AllowedVerbs.Contains(verb, StringComparer.OrdinalIgnoreCase)) {
            return CliCommandClass.VerbNotAllowed;
        }

        if (ContainsRejectedChars(arguments)) {
            return CliCommandClass.ArgumentsRejected;
        }

        var trimmed = arguments.Trim();

        if (_options.ReadOnlySubcommands.TryGetValue(verb, out var readOnly)
            && readOnly.Any(entry => MatchesTokenPrefix(trimmed, entry))) {
            return CliCommandClass.AllowedReadOnly;
        }

        return CliCommandClass.AllowedMutating;
    }

    // True when the string contains a control character that cannot be passed as
    // one ArgumentList token. Shell metacharacters (& | < > % ^) are ordinary
    // tokens under ArgumentList and are not rejected here.
    public static bool ContainsRejectedChars(string arguments) =>
        arguments.IndexOfAny(RejectedControlChars) >= 0;

    /// <summary>
    /// True when <paramref name="verb"/> <paramref name="arguments"/> drives automation
    /// execution (rpa run / debug / execution). These are gated by
    /// <see cref="UiPathCliOptions.EnableExecution"/> and must stay unreachable by default.
    /// </summary>
    public bool IsExecution(string verb, string arguments) {
        if (!_options.AllowedVerbs.Contains(verb, StringComparer.OrdinalIgnoreCase)
            || ContainsRejectedChars(arguments)) {
            return false;
        }

        var trimmed = arguments.Trim();
        return ExecutionSubcommands.TryGetValue(verb, out var entries)
            && entries.Any(entry => MatchesTokenPrefix(trimmed, entry));
    }

    /// <summary>
    /// <see cref="IsExecution"/> tolerating the verb-prefixed argument form: callers that build
    /// the executed command line pass "rpa run ..." as the arguments of verb "rpa", so the
    /// subcommand is the second token rather than the first.
    /// </summary>
    public bool IsExecutionCommand(string verb, string arguments) {
        if (IsExecution(verb, arguments)) {
            return true;
        }

        var tokens = ProcessRunner.SplitQuotedArguments(arguments);
        return tokens.Count >= 2
            && string.Equals(tokens[0], verb, StringComparison.OrdinalIgnoreCase)
            && IsExecution(verb, CliVerbArguments.ToArgumentString(tokens.Skip(1).ToList()));
    }

    /// <summary>True when automation execution is permitted on this server.</summary>
    public bool ExecutionEnabled => _options.EnableExecution;

    /// <summary>True when mutating subcommands are permitted on this server.</summary>
    public bool MutatingEnabled => _options.EnableMutatingCommands;

    // A read-only entry matches when the arguments start with it followed by a
    // space or end-of-string (case-insensitive), e.g. "project list" matches
    // "project list --output json" but not "project listing" or "project remove".
    private static bool MatchesTokenPrefix(string arguments, string entry) =>
        arguments.StartsWith(entry, StringComparison.OrdinalIgnoreCase)
        && (arguments.Length == entry.Length || arguments[entry.Length] == ' ');
}
