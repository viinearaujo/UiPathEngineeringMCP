using System.Text;

namespace UiPath.Engineering.Mcp.Providers.UiPathCli;

/// <summary>
/// Argument builders for the <c>uip rpa</c> verbs this server wires. Each builder returns
/// ArgumentList-shaped tokens (never a concatenated shell string); <see cref="ToArgumentString"/>
/// renders them for <see cref="IUiPathCliProvider.RunAsync"/>, whose tokenizer groups
/// double-quoted segments into one token.
/// </summary>
public static class CliVerbArguments {
    public const string RpaVerb = "rpa";

    /// <summary>Accepted <c>--scope</c> values for the Workflow Analyzer rule list.</summary>
    /// <remarks>
    /// The per-value constants exist so tool parameters can reference them from
    /// <c>[AllowedValues]</c>, which requires compile-time constants.
    /// </remarks>
    public const string AnalyzerScopeActivity = "Activity";
    public const string AnalyzerScopeWorkflow = "Workflow";
    public const string AnalyzerScopeProject = "Project";
    public const string AnalyzerScopeCodedWorkflow = "Coded Workflow";

    public static readonly string[] AnalyzerRuleScopes =
    [
        AnalyzerScopeActivity,
        AnalyzerScopeWorkflow,
        AnalyzerScopeProject,
        AnalyzerScopeCodedWorkflow
    ];

    /// <summary>Accepted <c>--log-level</c> values for the run/debug verbs.</summary>
    public const string LogLevelVerbose = "Verbose";
    public const string LogLevelTrace = "Trace";
    public const string LogLevelInformation = "Information";
    public const string LogLevelWarning = "Warning";
    public const string LogLevelError = "Error";
    public const string LogLevelCritical = "Critical";

    public static readonly string[] RunLogLevels =
    [
        LogLevelVerbose,
        LogLevelTrace,
        LogLevelInformation,
        LogLevelWarning,
        LogLevelError,
        LogLevelCritical
    ];

    /// <summary>Accepted <c>--profiling-mode</c> values for the run/debug start verbs.</summary>
    public const string ProfilingModeEndOfRun = "endOfRun";
    public const string ProfilingModeStream = "stream";

    public static readonly string[] ProfilingModes = [ProfilingModeEndOfRun, ProfilingModeStream];

    /// <summary>
    /// Mid-session debug commands that take <c>--wait-timeout-seconds</c> and return at the next
    /// stable state carrying DebugState/DebugDetails.
    /// </summary>
    public const string DebugStateCommand = "state";
    public const string DebugStepOverCommand = "step-over";
    public const string DebugStepIntoCommand = "step-into";
    public const string DebugStepOutCommand = "step-out";
    public const string DebugContinueCommand = "continue";
    public const string DebugContinueRetryCommand = "continue-retry";
    public const string DebugContinueIgnoreCommand = "continue-ignore";
    public const string DebugResumeCommand = "resume";
    public const string DebugBreakCommand = "break";
    public const string DebugRestartFromTopCommand = "restart-from-top";

    public static readonly string[] DebugSessionCommands =
    [
        DebugStateCommand,
        DebugStepOverCommand,
        DebugStepIntoCommand,
        DebugStepOutCommand,
        DebugContinueCommand,
        DebugContinueRetryCommand,
        DebugContinueIgnoreCommand,
        DebugResumeCommand,
        DebugBreakCommand,
        DebugRestartFromTopCommand
    ];

    /// <summary>
    /// Tool-facing command that ends the active execution. It maps to <c>rpa execution cancel</c>,
    /// which works for both <c>run</c> and <c>debug start</c>.
    /// </summary>
    public const string CancelCommand = "cancel";

    public static string[] AnalyzerRulesList(string projectPath, string scope) =>
        WithProject(
            ["analyzer-rules", "list", "--scope", scope],
            projectPath);

    /// <summary>
    /// The unscoped rule list. Enumerates every rule across every installed package and can take
    /// a minute or more — callers must opt into it explicitly.
    /// </summary>
    public static string[] AnalyzerRulesUnscoped(string projectPath) =>
        WithProject(["analyzer-rules", "list"], projectPath);

    public static string[] PackagesVersions(string projectPath, string packageId, bool includePrerelease) {
        var tokens = new List<string> { "packages", "versions", "--package-id", packageId };
        if (includePrerelease) {
            // Activity packages frequently ship -preview between stable releases, carrying the
            // freshest activity surface and .local/docs.
            tokens.Add("--include-prerelease");
        }

        return WithProject(tokens, projectPath);
    }

    /// <summary>
    /// One <c>--packages</c> occurrence per package, comma-joined key=value fields. Omitting the
    /// version resolves the latest compatible automatically, which is the preferred path.
    /// </summary>
    public static string[] PackagesInstall(string projectPath, IReadOnlyList<string> packageSpecs, string? nugetSourcesConfigPath = null) {
        var tokens = new List<string> { "packages", "install" };
        foreach (var spec in packageSpecs) {
            tokens.Add("--packages");
            tokens.Add(spec);
        }

        if (!string.IsNullOrWhiteSpace(nugetSourcesConfigPath)) {
            tokens.Add("--nuget-sources-config-path");
            tokens.Add(nugetSourcesConfigPath!);
        }

        return WithProject(tokens, projectPath);
    }

    public static string[] PackagesInspect(
        string projectPath,
        string? packageName,
        string? packageVersion,
        string? feedUrl,
        string? nupkgPath) {
        var tokens = new List<string> { "packages", "inspect" };
        AddOptional(tokens, "--package-name", packageName);
        AddOptional(tokens, "--package-version", packageVersion);
        AddOptional(tokens, "--feed-url", feedUrl);
        AddOptional(tokens, "--nupkg-path", nupkgPath);
        return WithProject(tokens, projectPath);
    }

    public static string[] ObjectRepositoryGet(string projectPath) =>
        WithProject(["get-object-repository"], projectPath);

    /// <summary>
    /// One <c>--library-paths</c> flag with the paths comma-separated; it is not a repeatable flag.
    /// </summary>
    public static string[] ObjectRepositoryGetLibrary(string projectPath, string libraryPathsCsv) =>
        WithProject(["get-library-object-repository", "--library-paths", libraryPathsCsv], projectPath);

    public static string[] Run(
        string projectPath,
        string filePath,
        IReadOnlyList<string>? inputArguments = null,
        string? logLevel = null,
        bool skipBuild = false,
        bool profiling = false,
        string? profilingMode = null) {
        var tokens = new List<string> { "run", "--file-path", filePath };
        AddInputArguments(tokens, inputArguments);
        AddOptional(tokens, "--log-level", logLevel);
        if (skipBuild) {
            tokens.Add("--skip-build");
        }

        if (profiling) {
            tokens.Add("--profiling");
        }

        AddOptional(tokens, "--profiling-mode", profilingMode);
        return WithProject(tokens, projectPath);
    }

    public static string[] DebugStart(
        string projectPath,
        string filePath,
        IReadOnlyList<string>? inputArguments = null,
        IReadOnlyList<string>? breakpoints = null,
        string? logLevel = null,
        bool skipBuild = false,
        bool profiling = false,
        string? profilingMode = null) {
        var tokens = new List<string> { "debug", "start", "--file-path", filePath };
        AddInputArguments(tokens, inputArguments);
        foreach (var breakpoint in breakpoints ?? []) {
            tokens.Add("--breakpoints");
            tokens.Add(breakpoint);
        }

        AddOptional(tokens, "--log-level", logLevel);
        if (skipBuild) {
            tokens.Add("--skip-build");
        }

        if (profiling) {
            tokens.Add("--profiling");
        }

        AddOptional(tokens, "--profiling-mode", profilingMode);
        return WithProject(tokens, projectPath);
    }

    /// <summary>
    /// A mid-session debug command. <paramref name="command"/> must be one of
    /// <see cref="DebugCommands"/> minus <c>start</c> and <c>cancel</c>.
    /// </summary>
    public static string[] DebugCommand(string projectPath, string command, int? waitTimeoutSeconds = null) {
        var tokens = new List<string> { "debug", command };
        if (waitTimeoutSeconds is { } seconds) {
            tokens.Add("--wait-timeout-seconds");
            tokens.Add(seconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        return WithProject(tokens, projectPath);
    }

    public static string[] DebugSetBreakpoints(string projectPath, IReadOnlyList<string> breakpoints) {
        var tokens = new List<string> { "debug", "set-breakpoints" };
        foreach (var breakpoint in breakpoints) {
            tokens.Add("--breakpoints");
            tokens.Add(breakpoint);
        }

        return WithProject(tokens, projectPath);
    }

    /// <summary>Cancels the active execution — works for both <c>run</c> and <c>debug start</c>.</summary>
    public static string[] ExecutionCancel(string projectPath) =>
        WithProject(["execution", "cancel"], projectPath);

    private static string[] WithProject(IEnumerable<string> tokens, string projectPath) {
        var all = new List<string>(tokens) { "--project-dir", projectPath, "--output", "json" };
        return [.. all];
    }

    private static void AddInputArguments(List<string> tokens, IReadOnlyList<string>? inputArguments) {
        foreach (var argument in inputArguments ?? []) {
            tokens.Add("--input-arguments");
            tokens.Add(argument);
        }
    }

    private static void AddOptional(List<string> tokens, string flag, string? value) {
        if (!string.IsNullOrWhiteSpace(value)) {
            tokens.Add(flag);
            tokens.Add(value!);
        }
    }

    /// <summary>
    /// Renders tokens for <see cref="IUiPathCliProvider.RunAsync"/> and
    /// <see cref="CliCommandPolicy.Classify"/>: each token becomes one ArgumentList entry, with
    /// tokens that need grouping wrapped in double quotes.
    /// </summary>
    public static string ToArgumentString(IReadOnlyList<string> tokens) {
        var builder = new StringBuilder();
        foreach (var token in tokens) {
            if (builder.Length > 0) {
                builder.Append(' ');
            }

            builder.Append(NeedsQuotes(token) ? $"\"{token}\"" : token);
        }

        return builder.ToString();
    }

    /// <summary>
    /// <see cref="CliCommandPolicy.Classify"/> takes the subcommand-relative arguments, while
    /// <see cref="IUiPathCliProvider.RunStructuredAsync"/> takes the whole command including the
    /// top-level verb. This drops the leading verb so one token list serves both.
    /// </summary>
    public static string SubcommandArguments(IReadOnlyList<string> tokens) =>
        tokens.Count < 2 ? string.Empty : ToArgumentString([.. tokens.Skip(1)]);

    /// <summary>
    /// <see cref="CliCommandPolicy.Classify"/> takes the subcommand-relative arguments, while
    /// <see cref="IUiPathCliProvider.RunAsync"/> takes them with the top-level verb prepended.
    /// </summary>
    public static string[] WithRpaVerb(IReadOnlyList<string> tokens) => [RpaVerb, .. tokens];

    /// <summary>
    /// True when a caller-supplied value cannot be passed as one inline token. Double quotes are
    /// stripped by Windows PowerShell 5.1 and control characters cannot be a single ArgumentList
    /// token at all; such values must go through a file (<c>key=@file</c>).
    /// </summary>
    public static bool IsUnpassableInline(string value) =>
        value.IndexOfAny(['\r', '\n', '\0', '"']) >= 0;

    private static bool NeedsQuotes(string token) =>
        token.Length == 0 || token.Any(char.IsWhiteSpace);
}
