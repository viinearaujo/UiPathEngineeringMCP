using System.ComponentModel;
using System.Diagnostics;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using UiPath.Engineering.Mcp.Core.Abstractions;
using UiPath.Engineering.Mcp.Core.Models;
using UiPath.Engineering.Mcp.Providers.UiPathCli;

namespace UiPath.Engineering.Mcp.Tools;

[McpServerToolType]
public sealed class CompileProjectTool {
    private readonly IUiPathCliProvider _cliProvider;
    private readonly IFilesystemProvider _filesystem;

    public CompileProjectTool(IUiPathCliProvider cliProvider, IFilesystemProvider filesystem) {
        _cliProvider = cliProvider;
        _filesystem = filesystem;
    }

    [McpServerTool(UseStructuredContent = true), Description("Leave-off CLI build (not on the Copilot default connector). Prefer validate_project(build:true), which already runs uip rpa build. Slower than get_compile_errors (in-memory Roslyn). Do not use as the agent-loop green gate — that is validate_project(build:false, pack:false). Next: validate_project.")]
    public async Task<ToolResult> CompileProject(
        [Description("Absolute path to the UiPath project directory.")] string projectPath,
        [Description("Optional progress sink. The MCP SDK binds this automatically when the client sent a progress token; do not pass it from a caller.")] IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken cancellationToken = default) {
        var sw = Stopwatch.StartNew();
        var reporter = CliToolSupport.ProgressFor(progress, "Running uip rpa build (a cold headless Studio start can take 30-90s).");

        if (ToolResults.GuardProject(_filesystem, projectPath, sw) is { } guardFailure) {
            return guardFailure;
        }

        var cliResult = await _cliProvider.ValidateAsync(projectPath, validate: false, build: true, pack: false, cancellationToken);
        reporter.Step(cliResult.Success ? "build succeeded." : "build reported errors.");

        return new ToolResult {
            Status = cliResult.Success ? "success" : "error",
            Summary = cliResult.Summary,
            Data = new {
                success = cliResult.Success,
                build = new {
                    executed = cliResult.Build.Executed,
                    success = cliResult.Build.Executed && cliResult.Build.Success,
                    errors = cliResult.Build.Errors,
                    warnings = cliResult.Build.Warnings
                },
                errors = cliResult.Errors,
                warnings = cliResult.Warnings
            },
            Errors = cliResult.Errors,
            Warnings = cliResult.Warnings,
            DurationMs = sw.ElapsedMilliseconds
        };
    }
}
