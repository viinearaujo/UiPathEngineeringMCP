using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using UiPath.Engineering.Mcp.Core.Abstractions;
using UiPath.Engineering.Mcp.Core.Models;
using UiPath.Engineering.Mcp.Providers.UiPathCli;

namespace UiPath.Engineering.Mcp.Tools;

[McpServerToolType]
public sealed class CreateProjectTool {
    private readonly IUiPathCliProvider _cliProvider;
    private readonly IFilesystemProvider _filesystem;

    public CreateProjectTool(IUiPathCliProvider cliProvider, IFilesystemProvider filesystem) {
        _cliProvider = cliProvider;
        _filesystem = filesystem;
    }

    [McpServerTool(UseStructuredContent = true), Description("Scaffolds a new UiPath project using 'uip rpa init'. Requires the UiPath CLI RPA tool installed on the host (uip tools install). Next: analyze_project.")]
    public async Task<ToolResult> CreateProject(
        [Description("Name of the new UiPath project (also becomes the project folder name).")] string name,
        [Description("Absolute path to the parent directory where the project folder is created. Must be inside the allowed roots.")] string parentDirectory,
        [Description("Expression language: CSharp or VisualBasic. Immutable after creation.")]
        [AllowedValues("CSharp", "VisualBasic")] string expressionLanguage = "CSharp",
        [Description("Target framework: Windows or Portable. Immutable after creation.")]
        [AllowedValues("Windows", "Portable")] string targetFramework = "Windows",
        [Description("Optional project description.")] string description = "",
        [Description("Optional progress sink. The MCP SDK binds this automatically when the client sent a progress token; do not pass it from a caller.")] IProgress<ProgressNotificationValue>? progress = null) {

        var sw = Stopwatch.StartNew();
        var reporter = CliToolSupport.ProgressFor(progress, "Running uip rpa init (a cold headless Studio start can take 30-90s).");

        if (string.IsNullOrWhiteSpace(name)) {
            return ToolResults.Failure("Project name is required.", sw);
        }

        if (ToolArgs.ParseChoice(expressionLanguage, "expressionLanguage", ["CSharp", "VisualBasic"], sw, out var parsedLanguage) is { } languageError) {
            return languageError;
        }

        if (ToolArgs.ParseChoice(targetFramework, "targetFramework", ["Windows", "Portable"], sw, out var parsedFramework) is { } frameworkError) {
            return frameworkError;
        }

        if (ToolResults.GuardAllowedPath(_filesystem, parentDirectory, sw) is { } guardFailure) {
            return guardFailure;
        }

        // Deliberate exception to the provider seam: this checks a directory that does
        // not exist yet (the CLI creates it), so it goes straight to System.IO.
        var targetDirectory = Path.Combine(Path.GetFullPath(parentDirectory), name);
        if (Directory.Exists(targetDirectory) && Directory.EnumerateFileSystemEntries(targetDirectory).Any()) {
            return ToolResults.Failure($"Target directory already exists and is not empty: {targetDirectory}", sw);
        }

        var arguments = $"init --name \"{name}\" --location \"{Path.GetFullPath(parentDirectory)}\" " +
            $"--expression-language {parsedLanguage} --target-framework {parsedFramework} " +
            $"--description \"{description}\" --output json";

        var cliResult = await _cliProvider.RunAsync("rpa", arguments);

        reporter.Step("uip rpa init returned.");

        // 'uip rpa init' can report failure while still creating the project files
        // (documented partial-success behavior), so the created artifact is the
        // ultimate source of truth.
        var createdProjectJson = _filesystem.FindProjectJson(targetDirectory);
        var succeeded = cliResult.Success || createdProjectJson != null;

        return new ToolResult {
            Status = succeeded ? "success" : "error",
            Summary = succeeded
                ? $"Project '{name}' scaffolded at '{targetDirectory}'."
                : $"Failed to scaffold project '{name}'. Ensure the UiPath CLI RPA tool is installed ('uip tools install').",
            Data = new {
                projectDirectory = targetDirectory,
                projectJson = createdProjectJson,
                cliReportedSuccess = cliResult.Success,
                partialSuccess = !cliResult.Success && createdProjectJson != null,
                command = cliResult.Command,
                exitCode = cliResult.ExitCode
            },
            Errors = succeeded ? [] : cliResult.Errors,
            Warnings = cliResult.Warnings,
            DurationMs = sw.ElapsedMilliseconds
        };
    }
}
