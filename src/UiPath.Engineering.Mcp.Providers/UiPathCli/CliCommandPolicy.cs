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

    // A read-only entry matches when the arguments start with it followed by a
    // space or end-of-string (case-insensitive), e.g. "project list" matches
    // "project list --output json" but not "project listing" or "project remove".
    private static bool MatchesTokenPrefix(string arguments, string entry) =>
        arguments.StartsWith(entry, StringComparison.OrdinalIgnoreCase)
        && (arguments.Length == entry.Length || arguments[entry.Length] == ' ');
}
