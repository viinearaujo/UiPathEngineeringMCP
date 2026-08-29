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

    [McpServerTool(UseStructuredContent = true), Description("Searches GitLab issues in the configured project and returns matching issue summaries.")]
    public async Task<ToolResult> SearchRepository(
        [Description("Search text matched against issue titles and descriptions.")] string query,
        [Description("Maximum number of issues to return (1-100).")] int maxResults = 10,
        CancellationToken cancellationToken = default) {

        var sw = Stopwatch.StartNew();

        if (string.IsNullOrWhiteSpace(query)) {
            return ToolResults.Failure("Query is required.", "Provide a non-empty search query.", sw);
        }

        var result = await _gitLab.SearchIssuesAsync(query, maxResults, cancellationToken);

        if (!result.Success) {
            return ToolResults.Failure("Repository search failed.", result.Errors, sw);
        }

        // The envelope already carries status/errors/warnings; Data holds only the payload.
        return ToolResults.Ok(
            $"Found {result.Issues.Count} issue(s).",
            new {
                results = result.Issues.Select(i => new {
                    iid = i.Iid,
                    title = i.Title,
                    state = i.State,
                    webUrl = i.WebUrl,
                    labels = i.Labels,
                    updatedAt = i.UpdatedAt
                }).ToList()
            }, sw);
    }
}
