using UiPath.Engineering.Mcp.Core.Abstractions;

namespace UiPath.Engineering.Mcp.Core.Docs;

public sealed class DocsSearchMatch {
    public string FilePath { get; init; } = string.Empty;
    public int Line { get; init; }
    public string Snippet { get; init; } = string.Empty;
    public string Kind { get; init; } = string.Empty;
}

public sealed class DocsSearchResult {
    public List<DocsSearchMatch> Matches { get; init; } = [];
    public List<string> SkippedFiles { get; init; } = [];
    public List<string> Warnings { get; init; } = [];
    public int FilesSearched { get; set; }
    public bool Truncated { get; set; }
    public string? Note { get; set; }
}

public sealed class ProjectDocsSearch {
    internal const int MaxFileCharacters = 2_000_000;
    private const int MaxSnippetLength = 300;
    internal const int MaxResults = 200;

    public const string KindMemory = "memory";
    public const string KindAdr = "adr";
    public const string KindContext = "context";
    public const string KindAll = "all";

    private readonly IFilesystemProvider _filesystem;
    private readonly ProjectKnowledgeStore _knowledge;
    private readonly ProjectAdrStore _adrs;

    public ProjectDocsSearch(IFilesystemProvider filesystem, ProjectKnowledgeStore knowledge, ProjectAdrStore adrs) {
        _filesystem = filesystem;
        _knowledge = knowledge;
        _adrs = adrs;
    }

    public DocsSearchResult Search(string projectPath, string query, string? kind) {
        var result = new DocsSearchResult();
        if (string.IsNullOrWhiteSpace(query)) {
            result.Warnings.Add("query is required.");
            return result;
        }

        var normalizedKind = string.IsNullOrWhiteSpace(kind) ? KindAll : kind.Trim().ToLowerInvariant();
        if (normalizedKind is not (KindMemory or KindAdr or KindContext or KindAll)) {
            result.Warnings.Add("kind must be memory, adr, context, or all.");
            return result;
        }

        var matches = new List<(DocsSearchMatch Match, bool Exact)>();
        foreach (var (path, fileKind) in FilesToSearch(projectPath, normalizedKind)) {
            string content;
            try {
                var fileSize = _filesystem.GetFileSize(path);
                if (fileSize > MaxFileCharacters) {
                    result.SkippedFiles.Add(path);
                    result.Warnings.Add($"Skipped oversized file '{path}' ({fileSize} bytes).");
                    continue;
                }

                content = _filesystem.ReadAllText(path);
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FileNotFoundException) {
                result.SkippedFiles.Add(path);
                result.Warnings.Add($"Skipped unreadable file '{path}': {ex.Message}");
                continue;
            }

            if (content.Length > MaxFileCharacters) {
                result.SkippedFiles.Add(path);
                result.Warnings.Add($"Skipped oversized file '{path}' ({content.Length} characters).");
                continue;
            }

            result.FilesSearched++;
            var lines = content.Split('\n');
            for (var i = 0; i < lines.Length; i++) {
                var exact = lines[i].Contains(query, StringComparison.Ordinal);
                if (!exact && !lines[i].Contains(query, StringComparison.OrdinalIgnoreCase)) {
                    continue;
                }

                matches.Add((new DocsSearchMatch {
                    FilePath = path,
                    Line = i + 1,
                    Snippet = TrimSnippet(lines[i]),
                    Kind = fileKind
                }, exact));
            }
        }

        foreach (var (match, _) in matches
            .OrderBy(m => m.Exact ? 0 : 1)
            .ThenBy(m => m.Match.FilePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(m => m.Match.Line)
            .Take(MaxResults)) {
            result.Matches.Add(match);
        }

        if (matches.Count > MaxResults) {
            result.Truncated = true;
            result.Note = $"Results truncated at {MaxResults} matches; narrow the query.";
        }

        return result;
    }

    private IEnumerable<(string Path, string Kind)> FilesToSearch(string projectPath, string kind) {
        if (kind is KindMemory or KindAll) {
            var index = ProjectDocsPaths.KnowledgeIndex(projectPath);
            if (_filesystem.FileExists(index)) {
                yield return (index, KindMemory);
            }

            foreach (var file in _knowledge.ListMarkdownFiles(projectPath)) {
                yield return (file, KindMemory);
            }
        }

        if (kind is KindAdr or KindAll) {
            var index = ProjectDocsPaths.AdrIndex(projectPath);
            if (_filesystem.FileExists(index)) {
                yield return (index, KindAdr);
            }

            foreach (var file in _adrs.ListMarkdownFiles(projectPath)) {
                yield return (file, KindAdr);
            }
        }

        if (kind is KindContext or KindAll) {
            var agents = ProjectDocsPaths.AgentsMd(projectPath);
            if (_filesystem.FileExists(agents)) {
                yield return (agents, KindContext);
            }

            var context = ProjectDocsPaths.ProjectContext(projectPath);
            if (_filesystem.FileExists(context)) {
                yield return (context, KindContext);
            }
        }
    }

    private static string TrimSnippet(string line) {
        var trimmed = line.TrimEnd('\r');
        return trimmed.Length <= MaxSnippetLength ? trimmed : trimmed[..MaxSnippetLength];
    }
}
