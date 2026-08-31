using System.ComponentModel;
using System.Diagnostics;
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

    [McpServerTool(UseStructuredContent = true), Description("Scaffolds a new UiPath project using 'uip rpa init'. Requires the UiPath CLI RPA tool installed on the host (uip tools install).")]
    public async Task<ToolResult> CreateProject(
        [Description("Name of the new UiPath project (also becomes the project folder name).")] string name,
        [Description("Absolute path to the parent directory where the project folder is created. Must be inside the allowed roots.")] string parentDirectory,
        [Description("Expression language: CSharp or VisualBasic. Immutable after creation.")] string expressionLanguage = "CSharp",
        [Description("Target framework: Windows or Portable. Immutable after creation.")] string targetFramework = "Windows",
        [Description("Optional project description.")] string description = "") {

        var sw = Stopwatch.StartNew();

        if (string.IsNullOrWhiteSpace(name)) {
            return ToolResults.Failure("Project name is required.", sw);
        }

        if (expressionLanguage is not ("CSharp" or "VisualBasic")) {
            return ToolResults.Failure("expressionLanguage must be 'CSharp' or 'VisualBasic'.", sw);
        }

        if (targetFramework is not ("Windows" or "Portable")) {
            return ToolResults.Failure("targetFramework must be 'Windows' or 'Portable'.", sw);
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
            $"--expression-language {expressionLanguage} --target-framework {targetFramework} " +
            $"--description \"{description}\" --output json";

        var cliResult = await _cliProvider.RunAsync("rpa", arguments);

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
