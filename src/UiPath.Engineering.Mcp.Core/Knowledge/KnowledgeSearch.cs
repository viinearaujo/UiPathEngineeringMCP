namespace UiPath.Engineering.Mcp.Core.Knowledge;

/// <summary>Where a corpus lives, which decides its reported source and its precedence.</summary>
public enum KnowledgeSource {
    /// <summary>The project's own installed package docs (<c>{PROJECT_DIR}/.local/docs/packages</c>).</summary>
    ProjectLocal,

    /// <summary>The vendored uipath-rpa references tree shipped with the server.</summary>
    Vendored
}

/// <summary>One searchable corpus: a root directory plus where it came from.</summary>
public sealed record KnowledgeCorpus(string Root, KnowledgeSource Source);

/// <summary>
/// One matching excerpt: the file and line the query hit, the nearest preceding heading,
/// and a few lines of context. The excerpt is the deliverable — a caller reads it instead
/// of the whole file.
/// </summary>
public sealed class KnowledgeExcerpt {
    public string FilePath { get; init; } = string.Empty;
    public string RelativePath { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty;
    public string? Package { get; init; }
    public string? Activity { get; init; }
    public int Line { get; init; }
    public string? Heading { get; init; }
    public string Snippet { get; init; } = string.Empty;
    public int Score { get; init; }
}

public sealed class KnowledgeSearchResult {
    public List<KnowledgeExcerpt> Excerpts { get; } = [];
    public List<string> RootsSearched { get; } = [];
    public List<string> Warnings { get; } = [];
    public int FilesSearched { get; set; }
    public bool Truncated { get; set; }
    public string? Note { get; set; }
}

/// <summary>
/// Substring search over markdown corpora with excerpt retrieval. Exact-case hits rank above
/// case-insensitive ones; an exact filename or heading match ranks above a body match; a file
/// where every query token appears ranks above one where only some do. Every excerpt carries the
/// line number and the nearest preceding heading, so a caller can cite the location instead of
/// reading the file.
/// </summary>
public static class KnowledgeSearchEngine {
    public const int DefaultMaxResults = 12;
    public const int MaxResults = 50;

    // At most two excerpts per file: a tool that returns a dozen lines of the same
    // document crowds out the other documents that matched.
    internal const int MaxPerFile = 2;
    internal const int MaxFileCharacters = FileReadLimits.MaxFileBytes;
    private const int ContextBefore = 2;
    private const int ContextAfter = 3;
    private const int MaxSnippetChars = 700;
    private const int MinTokenLength = 2;

    private static readonly char[] TokenSeparators =
        [' ', '-', '_', '.', ',', ';', ':', '/', '\\', '(', ')', '[', ']'];

    public static KnowledgeSearchResult Search(
        IReadOnlyList<KnowledgeCorpus> corpora,
        string? query,
        int maxResults = DefaultMaxResults,
        string? packageFilter = null,
        CancellationToken cancellationToken = default) {
        var result = new KnowledgeSearchResult();
        var trimmed = (query ?? string.Empty).Trim();
        if (trimmed.Length == 0) {
            result.Warnings.Add("query is required.");
            return result;
        }

        var limit = maxResults <= 0 ? DefaultMaxResults : Math.Min(maxResults, MaxResults);
        var tokens = Tokenize(trimmed);
        var scored = new List<Scored>();

        foreach (var corpus in corpora) {
            if (!Directory.Exists(corpus.Root)) {
                result.Warnings.Add($"Corpus root '{corpus.Root}' does not exist; it was skipped.");
                continue;
            }

            result.RootsSearched.Add(corpus.Root);
            foreach (var file in EnumerateMarkdown(corpus.Root, cancellationToken)) {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(corpus.Root, file).Replace('\\', '/');
                if (!MatchesPackage(relative, packageFilter)) {
                    continue;
                }

                var content = TryRead(file, result.Warnings);
                if (content is null) {
                    continue;
                }

                result.FilesSearched++;
                scored.AddRange(ScoreFile(file, relative, content, trimmed, tokens, corpus.Source));
            }
        }

        var ordered = scored
            .OrderByDescending(s => s.Excerpt.Score)
            .ThenBy(s => s.Excerpt.Source, StringComparer.Ordinal)
            .ThenBy(s => s.Excerpt.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.Excerpt.Line)
            .ToList();

        var perFile = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in ordered) {
            if (result.Excerpts.Count >= limit) {
                result.Truncated = true;
                break;
            }

            perFile.TryGetValue(candidate.Excerpt.FilePath, out var taken);
            if (taken >= MaxPerFile) {
                continue;
            }

            perFile[candidate.Excerpt.FilePath] = taken + 1;
            result.Excerpts.Add(candidate.Excerpt);
        }

        if (result.Truncated) {
            result.Note = $"Results truncated at {limit} excerpt(s); narrow the query or pass a larger maxResults.";
        }

        return result;
    }

    // A file-level match with no line hit still returns a head excerpt (title plus the
    // opening paragraph), so "what is X" answered by a document title is useful.
    private static IEnumerable<Scored> ScoreFile(
        string filePath, string relative, string content, string query, IReadOnlyList<string> tokens, KnowledgeSource source) {
        var lines = content.Split('\n');
        var descriptor = Describe(relative, source);
        var stem = Path.GetFileNameWithoutExtension(filePath);

        var nameScore = stem.Equals(query, StringComparison.OrdinalIgnoreCase)
            ? 100
            : stem.Contains(query, StringComparison.OrdinalIgnoreCase) ? 60 : 0;

        var best = new List<Scored>();

        string? heading = null;
        var headingHit = false;
        for (var i = 0; i < lines.Length; i++) {
            var line = lines[i].TrimEnd('\r');
            if (line.StartsWith('#')) {
                heading = line.TrimStart('#', ' ').Trim();
                if (heading.Contains(query, StringComparison.OrdinalIgnoreCase)) {
                    headingHit = true;
                }
            }

            if (!line.Contains(query, StringComparison.OrdinalIgnoreCase)) {
                continue;
            }

            var exact = line.Contains(query, StringComparison.Ordinal);
            best.Add(new Scored(new KnowledgeExcerpt {
                FilePath = filePath,
                RelativePath = relative,
                Source = SourceName(source),
                Package = descriptor.Package,
                Activity = descriptor.Activity,
                Line = i + 1,
                Heading = heading,
                Snippet = Excerpt(lines, i),
                Score = (exact ? 55 : 40) + nameScore + (headingHit ? 5 : 0)
            }, i, exact));
        }

        if (best.Count > 0) {
            return best;
        }

        var tokenScore = TokenScore(content, tokens);
        if (tokenScore <= 0) {
            return [];
        }

        // A document whose title matches but whose body does not is still a weak hit.
        var headScore = Math.Max(tokenScore, nameScore) + (headingHit ? 5 : 0);
        if (headScore <= 0) {
            return [];
        }

        return [new Scored(new KnowledgeExcerpt {
            FilePath = filePath,
            RelativePath = relative,
            Source = SourceName(source),
            Package = descriptor.Package,
            Activity = descriptor.Activity,
            Line = 1,
            Heading = descriptor.Activity,
            Snippet = HeadExcerpt(lines),
            Score = headScore
        }, 0, false)];
    }

    private static int TokenScore(string content, IReadOnlyList<string> tokens) {
        if (tokens.Count == 0) {
            return 0;
        }

        var matched = tokens.Count(t => content.Contains(t, StringComparison.OrdinalIgnoreCase));
        if (matched == 0) {
            return 0;
        }

        return matched == tokens.Count ? 25 : 10 * matched / tokens.Count;
    }

    private static string Excerpt(string[] lines, int index) {
        var start = Math.Max(0, index - ContextBefore);
        var end = Math.Min(lines.Length - 1, index + ContextAfter);
        var parts = new List<string>();
        for (var i = start; i <= end; i++) {
            var text = lines[i].TrimEnd('\r').Trim();
            if (text.Length > 0) {
                parts.Add(text);
            }
        }

        return Cap(string.Join('\n', parts));
    }

    private static string HeadExcerpt(string[] lines) {
        var parts = new List<string>();
        foreach (var line in lines) {
            var text = line.TrimEnd('\r').Trim();
            if (text.Length == 0) {
                continue;
            }

            parts.Add(text);
            if (parts.Count >= 4) {
                break;
            }
        }

        return Cap(string.Join('\n', parts));
    }

    private static string Cap(string text) =>
        text.Length <= MaxSnippetChars ? text : text[..MaxSnippetChars] + "...";

    private static bool MatchesPackage(string relative, string? packageFilter) {
        if (string.IsNullOrWhiteSpace(packageFilter)) {
            return true;
        }

        foreach (var segment in relative.Split('/', StringSplitOptions.RemoveEmptyEntries)) {
            if (segment.Equals(packageFilter, StringComparison.OrdinalIgnoreCase)) {
                return true;
            }
        }

        return false;
    }

    // The corpus layout is {package}/{version}/activities/{Activity}.md for package docs and
    // {guide}.md for the references root, so the first segment names a package only when it
    // looks like one and the leaf names an activity.
    private static (string? Package, string? Activity) Describe(string relative, KnowledgeSource source) {
        var parts = relative.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var package = source == KnowledgeSource.Vendored && parts.Length > 1
            && parts[0].Contains("Activities", StringComparison.OrdinalIgnoreCase)
                ? parts[0]
                : null;
        var activity = parts.Length > 0 && parts[^1].EndsWith(".md", StringComparison.OrdinalIgnoreCase)
            ? Path.GetFileNameWithoutExtension(parts[^1])
            : null;
        return (package, activity);
    }

    private static string SourceName(KnowledgeSource source) =>
        source == KnowledgeSource.ProjectLocal ? "project-local" : "vendored";

    private static string? TryRead(string file, List<string> warnings) {
        try {
            var info = new FileInfo(file);
            if (info.Exists && info.Length > MaxFileCharacters) {
                warnings.Add($"Skipped oversized file '{file}' ({info.Length} bytes).");
                return null;
            }

            var content = File.ReadAllText(file);
            if (content.Length > MaxFileCharacters) {
                warnings.Add($"Skipped oversized file '{file}' ({content.Length} characters).");
                return null;
            }

            return content;
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FileNotFoundException) {
            warnings.Add($"Skipped unreadable file '{file}': {ex.Message}");
            return null;
        }
    }

    // Manual recursion so one unreadable subdirectory does not abort the whole corpus walk.
    private static IEnumerable<string> EnumerateMarkdown(string root, CancellationToken cancellationToken) {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0) {
            cancellationToken.ThrowIfCancellationRequested();
            var current = pending.Pop();

            string[] files;
            string[] subDirectories;
            try {
                files = Directory.GetFiles(current, "*.md", SearchOption.TopDirectoryOnly);
                subDirectories = Directory.GetDirectories(current);
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                continue;
            }

            foreach (var file in files) {
                yield return file;
            }

            foreach (var sub in subDirectories) {
                pending.Push(sub);
            }
        }
    }

    private static IReadOnlyList<string> Tokenize(string query) {
        var tokens = new List<string>();
        foreach (var token in query.Split(
            TokenSeparators,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
            if (token.Length >= MinTokenLength && !tokens.Contains(token, StringComparer.OrdinalIgnoreCase)) {
                tokens.Add(token);
            }
        }

        return tokens;
    }

    private sealed record Scored(KnowledgeExcerpt Excerpt, int Index, bool Exact);
}