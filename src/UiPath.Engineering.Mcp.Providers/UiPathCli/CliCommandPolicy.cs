using UiPath.Engineering.Mcp.Core.Configuration;

namespace UiPath.Engineering.Mcp.Providers.UiPathCli;

public enum CliCommandClass { AllowedReadOnly, AllowedMutating, VerbNotAllowed }

// Decides whether a `uip <verb> <args>` invocation may run, based on
// UiPathCliOptions. Fails closed: unknown subcommands are treated as mutating.
public sealed class CliCommandPolicy {
    private readonly UiPathCliOptions _options;

    public CliCommandPolicy(UiPathCliOptions options) {
        _options = options;
    }

    public CliCommandClass Classify(string verb, string arguments) {
        if (!_options.AllowedVerbs.Contains(verb, StringComparer.OrdinalIgnoreCase)) {
            return CliCommandClass.VerbNotAllowed;
        }

        var subcommand = arguments.TrimStart()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault() ?? string.Empty;

        if (_options.ReadOnlySubcommands.TryGetValue(verb, out var readOnly)
            && readOnly.Contains(subcommand, StringComparer.OrdinalIgnoreCase)) {
            return CliCommandClass.AllowedReadOnly;
        }

        return CliCommandClass.AllowedMutating;
    }
}
