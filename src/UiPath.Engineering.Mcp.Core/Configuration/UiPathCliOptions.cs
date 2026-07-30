namespace UiPath.Engineering.Mcp.Core.Configuration;
public sealed class UiPathCliOptions {
    public string ExecutablePath { get; init; } = "uip";
    public int DefaultTimeoutSeconds { get; init; } = 300;
    public bool IncludeRawOutput { get; init; }

    // run_uip_cli allowlist. Only these top-level uip verbs may execute.
    public string[] AllowedVerbs { get; init; } = ["rpa", "solution"];

    // Subcommands of an allowed verb that run without EnableMutatingCommands.
    // Anything not listed here is classified as mutating (fail closed).
    public Dictionary<string, string[]> ReadOnlySubcommands { get; init; } = new(StringComparer.OrdinalIgnoreCase) {
        ["rpa"] = ["validate", "build"],
        ["solution"] = ["project list", "resources list", "deploy status"]
    };

    // Master switch for mutating subcommands (pack, publish, deploy, delete...).
    public bool EnableMutatingCommands { get; set; }

    // Character cap applied to each of stdout/stderr in run_uip_cli responses.
    public int MaxOutputChars { get; init; } = 32768;
}
