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
/// BM25 search over markdown corpora with excerpt retrieval. Document scores come from an
/// in-memory inverted index (built once per root, invalidated on markdown mtime change).
/// Filename and heading matches still boost ranking; exact-case line hits outrank
/// case-insensitive ones. Every excerpt carries the line number and the nearest preceding
/// heading, so a caller can cite the location instead of reading the file.
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

    // Integer score = scaled BM25 + discrete boosts so ordering stays stable for callers.
    private const int Bm25Scale = 100;
    private const int ExactFilenameBoost = 100;
    private const int PartialFilenameBoost = 60;
    private const int ExactCaseBoost = 15;
    private const int HeadingBoost = 5;
    private const int PhraseBoost = 10;

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
        var tokens = KnowledgeIndex.TokenizeQuery(trimmed);
        var scored = new List<Scored>();

        foreach (var corpus in corpora) {
            if (!Directory.Exists(corpus.Root)) {
                result.Warnings.Add($"Corpus root '{corpus.Root}' does not exist; it was skipped.");
                continue;
            }

            result.RootsSearched.Add(corpus.Root);
            var index = KnowledgeIndex.ForRoot(corpus.Root).GetOrBuild(result.Warnings, cancellationToken);
            result.FilesSearched += index.Docs.Count;

            for (var docId = 0; docId < index.Docs.Count; docId++) {
                cancellationToken.ThrowIfCancellationRequested();
                var doc = index.Docs[docId];
                if (!MatchesPackage(doc.RelativePath, packageFilter)) {
                    continue;
                }

                scored.AddRange(ScoreDocument(index, docId, doc, trimmed, tokens, corpus.Source));
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

    private static IEnumerable<Scored> ScoreDocument(
        KnowledgeIndex.IndexSnapshot index,
        int docId,
        KnowledgeIndex.IndexedDocument doc,
        string query,
        IReadOnlyList<string> tokens,
        KnowledgeSource source) {
        var bm25 = KnowledgeIndex.Bm25(index, tokens, docId);
        var lines = doc.Content.Split('\n');
        var descriptor = Describe(doc.RelativePath, source);
        var stem = Path.GetFileNameWithoutExtension(doc.FilePath);

        var nameScore = stem.Equals(query, StringComparison.OrdinalIgnoreCase)
            ? ExactFilenameBoost
            : stem.Contains(query, StringComparison.OrdinalIgnoreCase) ? PartialFilenameBoost : 0;

        var baseScore = (int)Math.Round(bm25 * Bm25Scale) + nameScore;
        var best = new List<Scored>();

        string? heading = null;
        var headingHit = false;
        for (var i = 0; i < lines.Length; i++) {
            var line = lines[i].TrimEnd('\r');
            if (line.StartsWith('#')) {
                heading = line.TrimStart('#', ' ').Trim();
                if (heading.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || tokens.Any(t => heading.Contains(t, StringComparison.OrdinalIgnoreCase))) {
                    headingHit = true;
                }
            }

            if (!LineMatches(line, query, tokens)) {
                continue;
            }

            var exact = line.Contains(query, StringComparison.Ordinal);
            var phrase = query.Length >= 2 && line.Contains(query, StringComparison.OrdinalIgnoreCase);
            var score = baseScore
                + (exact ? ExactCaseBoost : 0)
                + (phrase ? PhraseBoost : 0)
                + (headingHit ? HeadingBoost : 0);

            // A filename-only boost with no BM25 still needs a positive score to surface.
            if (score <= 0 && nameScore > 0) {
                score = nameScore + (headingHit ? HeadingBoost : 0);
            }

            // Substring line hit whose tokens did not land in the inverted index (e.g. a
            // query that is only a prefix of an indexed token) still yields a weak excerpt.
            if (score <= 0) {
                score = 1 + (exact ? ExactCaseBoost : 0) + (headingHit ? HeadingBoost : 0);
            }

            best.Add(new Scored(new KnowledgeExcerpt {
                FilePath = doc.FilePath,
                RelativePath = doc.RelativePath,
                Source = SourceName(source),
                Package = descriptor.Package,
                Activity = descriptor.Activity,
                Line = i + 1,
                Heading = heading,
                Snippet = Excerpt(lines, i),
                Score = score
            }, i, exact));
        }

        if (best.Count > 0) {
            return best;
        }

        // Title / filename hit with no line match: return a head excerpt.
        var headScore = baseScore + (headingHit ? HeadingBoost : 0);
        if (headScore <= 0) {
            return [];
        }

        return [new Scored(new KnowledgeExcerpt {
            FilePath = doc.FilePath,
            RelativePath = doc.RelativePath,
            Source = SourceName(source),
            Package = descriptor.Package,
            Activity = descriptor.Activity,
            Line = 1,
            Heading = descriptor.Activity,
            Snippet = HeadExcerpt(lines),
            Score = headScore
        }, 0, false)];
    }

    private static bool LineMatches(string line, string query, IReadOnlyList<string> tokens) {
        if (line.Contains(query, StringComparison.OrdinalIgnoreCase)) {
            return true;
        }

        return tokens.Count > 0 && tokens.Any(t => line.Contains(t, StringComparison.OrdinalIgnoreCase));
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

    private sealed record Scored(KnowledgeExcerpt Excerpt, int Index, bool Exact);
}

