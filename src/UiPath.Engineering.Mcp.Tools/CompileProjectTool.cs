using System.ComponentModel;
using System.Diagnostics;
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

    [McpServerTool, Description("Compiles a UiPath project using the authoritative UiPath CLI build step (uip rpa build) and returns structured compiler errors and warnings. Slower than get_compile_errors but is the ground-truth build. Requires the UiPath CLI on the host.")]
    public async Task<ToolResult> CompileProject(
        [Description("Absolute path to the UiPath project directory.")] string projectPath,
        CancellationToken cancellationToken = default) {
        var sw = Stopwatch.StartNew();

        if (ToolResults.GuardProject(_filesystem, projectPath, sw) is { } guardFailure) {
            return guardFailure;
        }

        try {
            var cliResult = await _cliProvider.ValidateAsync(projectPath, validate: false, build: true, pack: false, cancellationToken);

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
        } catch (Exception ex) {
            return ToolResults.FromException(ex, "Project compilation failed.", sw);
        }
    }
}
