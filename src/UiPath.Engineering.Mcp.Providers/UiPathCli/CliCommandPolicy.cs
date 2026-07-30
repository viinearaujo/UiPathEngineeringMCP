using UiPath.Engineering.Mcp.Core.Configuration;

namespace UiPath.Engineering.Mcp.Providers.UiPathCli;

public enum CliCommandClass { AllowedReadOnly, AllowedMutating, VerbNotAllowed, ArgumentsRejected }

// Decides whether a `uip <verb> <args>` invocation may run, based on
// UiPathCliOptions. Fails closed: unknown subcommands are treated as mutating.
// Arguments containing shell metacharacters are rejected outright: .cmd/.bat
// shims are launched through cmd.exe /c, so & | < > % ^ (and newlines) could
// break out of the quoted command and execute arbitrary shell. Double quotes
// are allowed because quoted paths with spaces are legitimate.
public sealed class CliCommandPolicy {
    private static readonly char[] RejectedArgumentChars = ['&', '|', '<', '>', '%', '^', '\r', '\n'];

    private readonly UiPathCliOptions _options;

    public CliCommandPolicy(UiPathCliOptions options) {
        _options = options;
    }

    public CliCommandClass Classify(string verb, string arguments) {
        if (!_options.AllowedVerbs.Contains(verb, StringComparer.OrdinalIgnoreCase)) {
            return CliCommandClass.VerbNotAllowed;
        }

        if (arguments.IndexOfAny(RejectedArgumentChars) >= 0) {
            return CliCommandClass.ArgumentsRejected;
        }

        var trimmed = arguments.Trim();

        if (_options.ReadOnlySubcommands.TryGetValue(verb, out var readOnly)
            && readOnly.Any(entry => MatchesTokenPrefix(trimmed, entry))) {
            return CliCommandClass.AllowedReadOnly;
        }

        return CliCommandClass.AllowedMutating;
    }

    // A read-only entry matches when the arguments start with it followed by a
    // space or end-of-string (case-insensitive), e.g. "project list" matches
    // "project list --output json" but not "project listing" or "project remove".
    private static bool MatchesTokenPrefix(string arguments, string entry) =>
        arguments.StartsWith(entry, StringComparison.OrdinalIgnoreCase)
        && (arguments.Length == entry.Length || arguments[entry.Length] == ' ');
}
