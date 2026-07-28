using System.ComponentModel;
using System.Diagnostics;
using ModelContextProtocol.Server;
using UiPath.Engineering.Mcp.Core.Models;
using UiPath.Engineering.Mcp.Providers.GitLab;

namespace UiPath.Engineering.Mcp.Tools;

public sealed class WorkItemInput {
    [Description("Issue title (required).")]
    public string Title { get; set; } = string.Empty;

    [Description("Issue description / body.")]
    public string Description { get; set; } = string.Empty;

    [Description("Optional labels to apply to the issue.")]
    public List<string>? Labels { get; set; }
}

[McpServerToolType]
public sealed class CreateWorkItemsTool {
    private readonly IGitLabProvider _gitLab;

    public CreateWorkItemsTool(IGitLabProvider gitLab) => _gitLab = gitLab;

    [McpServerTool, Description("Creates GitLab issues (work items) in the configured project and reports which ones were created and which failed.")]
    public async Task<ToolResult> CreateWorkItems(
        [Description("Array of work items to create, each with title, description, and optional labels.")] List<WorkItemInput> items) {

        var sw = Stopwatch.StartNew();

        if (items is null || items.Count == 0) {
            return new ToolResult {
                Status = "error",
                Summary = "No work items provided.",
                Data = new { success = false, created = Array.Empty<object>(), failed = Array.Empty<object>(), errors = new[] { "Provide at least one work item." }, warnings = Array.Empty<string>() },
                Errors = ["Provide at least one work item."],
                DurationMs = sw.ElapsedMilliseconds
            };
        }

        var created = new List<object>();
        var failed = new List<object>();
        var errors = new List<string>();

        foreach (var item in items) {
            if (string.IsNullOrWhiteSpace(item.Title)) {
                failed.Add(new { title = item.Title ?? string.Empty, error = "Title is required." });
                errors.Add("Title is required.");
                continue;
            }

            var result = await _gitLab.CreateIssueAsync(item.Title, item.Description ?? string.Empty, item.Labels ?? []);

            if (result.Success && result.Issue is not null) {
                created.Add(new { iid = result.Issue.Iid, title = result.Issue.Title, webUrl = result.Issue.WebUrl });
            }
            else {
                var error = string.Join(" ", result.Errors);
                failed.Add(new { title = item.Title, error });
                errors.Add(error);
            }
        }

        var success = failed.Count == 0;

        return new ToolResult {
            Status = success ? "success" : "error",
            Summary = $"Created {created.Count} work item(s), {failed.Count} failed.",
            Data = new {
                success,
                created,
                failed,
                errors,
                warnings = Array.Empty<string>()
            },
            Errors = errors,
            DurationMs = sw.ElapsedMilliseconds
        };
    }
}
