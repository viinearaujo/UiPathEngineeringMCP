using System.ComponentModel;
using System.Diagnostics;
using ModelContextProtocol.Server;
using UiPath.Engineering.Mcp.Core.Abstractions;
using UiPath.Engineering.Mcp.Core.Models;
using UiPath.Engineering.Mcp.Core.Parsing;

namespace UiPath.Engineering.Mcp.Tools;

[McpServerToolType]
public sealed class ExplainWorkflowTool {
    private readonly IFilesystemProvider _filesystem;
    private readonly IProjectModelBuilder _modelBuilder;

    public ExplainWorkflowTool(IFilesystemProvider filesystem, IProjectModelBuilder modelBuilder) {
        _filesystem = filesystem;
        _modelBuilder = modelBuilder;
    }

    [McpServerTool, Description("Explains a single workflow in a UiPath project: arguments, variables, activity outline, exception handlers, invoked workflows, and log messages.")]
    public async Task<ToolResult> ExplainWorkflow(
        [Description("Absolute path to the UiPath project directory.")] string projectPath,
        [Description("Workflow file to explain (file name, with or without .xaml, or a path).")] string workflowFile,
        CancellationToken cancellationToken = default) {
        var sw = Stopwatch.StartNew();

        if (ToolResults.GuardAllowedPath(_filesystem, projectPath, sw) is { } guardFailure) {
            return guardFailure;
        }

        try {
            var model = await _modelBuilder.BuildAsync(projectPath, cancellationToken);

            var requestedName = Path.GetFileName(workflowFile);
            if (!requestedName.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)) {
                requestedName += ".xaml";
            }

            var workflow = model.Workflows.FirstOrDefault(w =>
                string.Equals(w.FileName, requestedName, StringComparison.OrdinalIgnoreCase));

            if (workflow is null) {
                var available = model.Workflows.Select(w => w.FileName).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
                return new ToolResult {
                    Status = "error",
                    Summary = $"Workflow '{requestedName}' not found.",
                    Errors = [$"Workflow '{requestedName}' was not found in project '{model.ProjectName}'."],
                    Data = new { AvailableWorkflows = available },
                    DurationMs = sw.ElapsedMilliseconds
                };
            }

            var data = new {
                workflow.FileName,
                workflow.FilePath,
                workflow.IsMain,
                Arguments = workflow.Arguments.Select(a => new { a.Name, a.Direction, a.Type }).ToList(),
                Variables = workflow.Variables.Select(v => new { v.Name, v.Type, v.Scope }).ToList(),
                Activities = workflow.Activities.Select(a => new { a.DisplayName, a.Type, a.Depth }).ToList(),
                ExceptionHandlers = workflow.ExceptionHandlers.Select(e => new { e.HasGlobalHandler, e.CatchTypes }).ToList(),
                InvokeWorkflows = workflow.InvokeWorkflows.Select(i => new { i.DisplayName, i.TargetWorkflow }).ToList(),
                LogMessages = workflow.LogMessages.Select(l => new { l.DisplayName, l.Level, l.Message }).ToList(),
                workflow.HasParseError,
                workflow.ParseError
            };

            var warnings = workflow.HasParseError
                ? new List<string> { $"Workflow could not be fully parsed: {workflow.ParseError}" }
                : [];

            return ToolResults.Ok(
                $"Workflow '{workflow.FileName}': {workflow.Arguments.Count} arguments, " +
                $"{workflow.Variables.Count} variables, {workflow.Activities.Count} activities, " +
                $"{workflow.ExceptionHandlers.Count} exception handlers, invokes {workflow.InvokeWorkflows.Count} workflows.",
                data, sw, warnings);
        } catch (Exception ex) {
            // Never surface a raw exception/stack trace to the MCP client.
            return ToolResults.FromException(ex, "Workflow explanation failed.", sw);
        }
    }
}
