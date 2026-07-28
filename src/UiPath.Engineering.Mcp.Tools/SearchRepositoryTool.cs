using System.ComponentModel;
using System.Diagnostics;
using ModelContextProtocol.Server;
using UiPath.Engineering.Mcp.Core.Models;
using UiPath.Engineering.Mcp.Providers.GitLab;

namespace UiPath.Engineering.Mcp.Tools;

[McpServerToolType]
public sealed class SearchRepositoryTool {
    private readonly IGitLabProvider _gitLab;

    public SearchRepositoryTool(IGitLabProvider gitLab) => _gitLab = gitLab;

    [McpServerTool, Description("Searches GitLab issues in the configured project and returns matching issue summaries.")]
    public async Task<ToolResult> SearchRepository(
        [Description("Search text matched against issue titles and descriptions.")] string query,
        [Description("Maximum number of issues to return (1-100).")] int maxResults = 10) {

        var sw = Stopwatch.StartNew();

        if (string.IsNullOrWhiteSpace(query)) {
            return new ToolResult {
                Status = "error",
                Summary = "Query is required.",
                Data = new { success = false, results = Array.Empty<object>(), errors = new[] { "Provide a non-empty search query." }, warnings = Array.Empty<string>() },
                Errors = ["Provide a non-empty search query."],
                DurationMs = sw.ElapsedMilliseconds
            };
        }

        var result = await _gitLab.SearchIssuesAsync(query, maxResults);

        if (!result.Success) {
            return new ToolResult {
                Status = "error",
                Summary = "Repository search failed.",
                Data = new { success = false, results = Array.Empty<object>(), errors = result.Errors, warnings = Array.Empty<string>() },
                Errors = result.Errors,
                DurationMs = sw.ElapsedMilliseconds
            };
        }

        return new ToolResult {
            Status = "success",
            Summary = $"Found {result.Issues.Count} issue(s).",
            Data = new {
                success = true,
                results = result.Issues.Select(i => new {
                    iid = i.Iid,
                    title = i.Title,
                    state = i.State,
                    webUrl = i.WebUrl,
                    labels = i.Labels,
                    updatedAt = i.UpdatedAt
                }).ToList(),
                errors = Array.Empty<string>(),
                warnings = Array.Empty<string>()
            },
            DurationMs = sw.ElapsedMilliseconds
        };
    }
}
