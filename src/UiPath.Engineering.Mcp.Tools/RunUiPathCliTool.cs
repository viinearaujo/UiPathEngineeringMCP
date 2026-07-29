using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using UiPath.Engineering.Mcp.Core;
using UiPath.Engineering.Mcp.Core.Abstractions;
using UiPath.Engineering.Mcp.Core.Configuration;
using UiPath.Engineering.Mcp.Core.Models;
using UiPath.Engineering.Mcp.Providers.UiPathCli;

namespace UiPath.Engineering.Mcp.Tools;

[McpServerToolType]
public sealed class RunUiPathCliTool {
    private readonly IUiPathCliProvider _cli;
    private readonly IFilesystemProvider _filesystem;
    private readonly CliCommandPolicy _policy;
    private readonly UiPathCliOptions _options;

    public RunUiPathCliTool(
        IUiPathCliProvider cli,
        IFilesystemProvider filesystem,
        CliCommandPolicy policy,
        IOptions<UiPathCliOptions> options) {
        _cli = cli;
        _filesystem = filesystem;
        _policy = policy;
        _options = options.Value;
    }

    [McpServerTool, Description("Runs an allowlisted UiPath CLI (uip) command and returns structured output. Allowed verbs are configured server-side (default: rpa, solution); mutating subcommands are blocked unless enabled in server config. stdout/stderr are redacted and capped.")]
    public async Task<ToolResult> RunUiPathCli(
        [Description("Top-level uip verb, e.g. 'rpa' or 'solution'.")] string verb,
        [Description("Arguments appended verbatim after the verb, e.g. 'validate --project-dir \"C:/proj\" --output json'.")] string arguments,
        [Description("Optional working directory; must be inside an allowed project root.")] string? workingDirectory = null,
        CancellationToken cancellationToken = default) {

        var sw = Stopwatch.StartNew();

        if (string.IsNullOrWhiteSpace(verb)) {
            return ToolResults.Failure("verb is required.", sw);
        }
        if (string.IsNullOrWhiteSpace(arguments)) {
            return ToolResults.Failure("arguments is required.", sw);
        }

        var classification = _policy.Classify(verb, arguments);
        if (classification == CliCommandClass.VerbNotAllowed) {
            return ToolResults.Failure($"Verb '{verb}' is not allowed.",
                [new ToolError(ToolErrorCodes.CliVerbNotAllowed,
                    $"The verb '{verb}' is not in the server allowlist.",
                    $"Use one of: {string.Join(", ", _options.AllowedVerbs)}.")], sw);
        }

        if (classification == CliCommandClass.AllowedMutating && !_options.EnableMutatingCommands) {
            var subcommand = arguments.TrimStart().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            return ToolResults.Failure("Mutating command blocked.",
                [new ToolError(ToolErrorCodes.MutatingCommandDisabled,
                    $"'{verb} {subcommand}' is classified as mutating and mutating commands are disabled on this server.",
                    "Set UiPathCli:EnableMutatingCommands to true in appsettings.json and restart the server.")], sw);
        }

        if (workingDirectory is not null
            && ToolResults.GuardAllowedPath(_filesystem, workingDirectory, sw) is { } guardFailure) {
            return guardFailure;
        }

        var result = await _cli.RunAsync(verb, arguments, workingDirectory, cancellationToken);

        return new ToolResult {
            Status = result.Success ? "success" : "error",
            Summary = result.Summary,
            Data = new {
                command = result.Command,
                exitCode = result.ExitCode,
                success = result.Success,
                stdout = result.StdOut,
                stderr = result.StdErr,
                errors = result.Errors,
                warnings = result.Warnings
            },
            Errors = result.Errors,
            Warnings = result.Warnings,
            DurationMs = sw.ElapsedMilliseconds
        };
    }
}
