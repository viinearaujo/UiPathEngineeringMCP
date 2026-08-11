using UiPath.Engineering.Mcp.Core.Abstractions;
using UiPath.Engineering.Mcp.Core.CodeAnalysis;
using UiPath.Engineering.Mcp.Core.Parsing;

namespace UiPath.Engineering.Mcp.Core.CodeSearch;

/// <summary>
/// Substring search over a UiPath project's .xaml and .cs files. Text mode scans
/// file contents line-by-line; symbol mode reuses SP1's cached compilation;
/// activity/workflow modes reuse the cached UiPathProjectModel. Stateless beyond
/// the injected caches; every method takes the project path.
/// </summary>
public sealed class CodebaseSearchService : ICodebaseSearchService {
    // ~2 MB of text: files larger than this are skipped rather than scanned.
    internal const int MaxFileCharacters = 2_000_000;
    private const int MaxSnippetLength = 300;

    private readonly ICSharpContextBuilder _contextBuilder;
    private readonly IProjectModelBuilder _projectModelBuilder;
    private readonly IFilesystemProvider _filesystem;

    public CodebaseSearchService(
        ICSharpContextBuilder contextBuilder,
        IProjectModelBuilder projectModelBuilder,
        IFilesystemProvider filesystem) {
        _contextBuilder = contextBuilder;
        _projectModelBuilder = projectModelBuilder;
        _filesystem = filesystem;
    }

    public Task<TextSearchResult> SearchTextAsync(string projectPath, string query, CancellationToken cancellationToken = default) {
        var result = new TextSearchResult();
        var matches = new List<(TextMatch Match, bool Exact)>();

        var files = _filesystem.FindXamlFiles(projectPath).Concat(_filesystem.FindCSharpFiles(projectPath));
        foreach (var file in files) {
            cancellationToken.ThrowIfCancellationRequested();
            string content;
            try {
                content = _filesystem.ReadAllText(file);
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FileNotFoundException) {
                result.SkippedFiles.Add(file);
                result.Warnings.Add($"Skipped unreadable file '{file}': {ex.Message}");
                continue;
            }
            if (content.Length > MaxFileCharacters) {
                result.SkippedFiles.Add(file);
                result.Warnings.Add($"Skipped oversized file '{file}' ({content.Length} characters).");
                continue;
            }
            result.FilesSearched++;

            var lines = content.Split('\n');
            for (var i = 0; i < lines.Length; i++) {
                var exact = lines[i].Contains(query, StringComparison.Ordinal);
                if (!exact && !lines[i].Contains(query, StringComparison.OrdinalIgnoreCase)) {
                    continue;
                }
                matches.Add((new TextMatch { FilePath = file, Line = i + 1, Snippet = TrimSnippet(lines[i]) }, exact));
            }
        }

        foreach (var (match, _) in matches
            .OrderBy(m => m.Exact ? 0 : 1)
            .ThenBy(m => m.Match.FilePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(m => m.Match.Line)
            .Take(CSharpAnalysisService.MaxResults)) {
            result.Matches.Add(match);
        }
        if (matches.Count > CSharpAnalysisService.MaxResults) {
            result.Truncated = true;
            result.Note = $"Results truncated at {CSharpAnalysisService.MaxResults} matches; narrow the query.";
        }
        return Task.FromResult(result);
    }

    private static string TrimSnippet(string line) {
        var trimmed = line.Trim();
        return trimmed.Length <= MaxSnippetLength ? trimmed : trimmed[..MaxSnippetLength] + "…";
    }
}
