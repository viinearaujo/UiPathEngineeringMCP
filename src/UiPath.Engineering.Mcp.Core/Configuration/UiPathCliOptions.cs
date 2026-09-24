namespace UiPath.Engineering.Mcp.Core.Configuration;
public sealed class UiPathCliOptions {
    public string ExecutablePath { get; init; } = "uip";
    public int DefaultTimeoutSeconds { get; init; } = 300;
    public bool IncludeRawOutput { get; init; }

    // run_ui_path_cli allowlist. Only these top-level uip verbs may execute.
    public string[] AllowedVerbs { get; init; } = ["rpa", "solution"];

    // Subcommands of an allowed verb that run without EnableMutatingCommands.
    // Anything not listed here is classified as mutating (fail closed).
    public Dictionary<string, string[]> ReadOnlySubcommands { get; init; } = new(StringComparer.OrdinalIgnoreCase) {
        ["rpa"] = [
            "validate",
            "build",
            "analyzer-rules list",
            "packages versions",
            "packages inspect",
            "get-object-repository",
            "get-library-object-repository"
        ],
        ["solution"] = ["project list", "resources list", "deploy status"]
    };

    // Master switch for mutating subcommands (pack, publish, deploy, delete...).
    public bool EnableMutatingCommands { get; set; }

    // Master switch for subcommands that execute automation (rpa run, rpa debug,
    // rpa execution). Running a workflow executes arbitrary code on this machine,
    // so it is gated separately from EnableMutatingCommands and stays off unless an
    // operator opts in. Fail closed: see CliCommandPolicy.ExecutionSubcommands.
    public bool EnableExecution { get; set; }

    public Dictionary<string, string>? Environment { get; init; }

    // Character cap applied to each of stdout/stderr in run_ui_path_cli responses.
    public int MaxOutputChars { get; init; } = 32768;
}
