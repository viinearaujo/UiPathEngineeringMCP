using System.ComponentModel;
using System.Diagnostics;
using ModelContextProtocol.Server;
using UiPath.Engineering.Mcp.Core.Abstractions;
using UiPath.Engineering.Mcp.Core.Docs;
using UiPath.Engineering.Mcp.Core.Models;
using UiPath.Engineering.Mcp.Core.Parsing;

namespace UiPath.Engineering.Mcp.Tools;

[McpServerToolType]
public sealed class ValidateProjectDocsTool {
    private readonly IFilesystemProvider _filesystem;
    private readonly IProjectModelBuilder _modelBuilder;
    private readonly ProjectDocsValidator _validator;

    public ValidateProjectDocsTool(
        IFilesystemProvider filesystem,
        IProjectModelBuilder modelBuilder,
        ProjectDocsValidator validator) {
        _filesystem = filesystem;
        _modelBuilder = modelBuilder;
        _validator = validator;
    }

    [McpServerTool(UseStructuredContent = true), Description("Inspects project docs without changing plan state. Wiki hygiene only — findings do not block update_plan_task(done). verify_work still refuses auto-done on docs errors.")]
    public async Task<ToolResult> ValidateProjectDocs(
        [Description("Absolute path to the UiPath project directory (must contain project.json).")] string projectPath,
        CancellationToken cancellationToken = default) {

        var sw = Stopwatch.StartNew();

        if (ToolResults.GuardProject(_filesystem, projectPath, sw) is { } guardFailure) {
            return guardFailure;
        }

        UiPathProjectModel model;
        try {
            model = await _modelBuilder.BuildAsync(projectPath, cancellationToken);
        } catch (Exception ex) {
            return ToolResults.FromException(ex, "Project analysis failed.", sw);
        }

        var findings = _validator.Validate(projectPath, model);
        var errors = findings.Count(f => f.Severity == DocsFinding.Error);
        var warnings = findings.Where(f => f.Severity == DocsFinding.Warning).Select(f => f.Message).ToList();
        var summary = errors == 0
            ? (warnings.Count == 0 ? "Project docs are current." : "Project docs have warnings only.")
            : $"Project docs have {errors} error finding(s).";

        if (errors > 0) {
            return ToolResults.Failure(summary, findings.Where(f => f.Severity == DocsFinding.Error).Select(DocsGate.ToToolError).ToList(), sw);
        }

        return ToolResults.Ok(summary, new { findings, errorCount = errors, warningCount = warnings.Count }, sw, warnings);
    }
}
