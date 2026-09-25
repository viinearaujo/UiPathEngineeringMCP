using System.Collections.Concurrent;
using System.Text;

namespace UiPath.Engineering.Mcp.Core.Knowledge;

/// <summary>
/// Per-root inverted index with BM25 scoring. Postings store only (docId, tf);
/// document text is retained solely for excerpt windows. A root is rebuilt when
/// any markdown file's last-write time under it changes.
/// </summary>
internal sealed class KnowledgeIndex {
    internal const double K1 = 1.2;
    internal const double B = 0.75;

    private static readonly ConcurrentDictionary<string, KnowledgeIndex> Indexes =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly char[] TokenSeparators =
        [' ', '-', '_', '.', ',', ';', ':', '/', '\\', '(', ')', '[', ']', '#', '|', '"', '\'', '`', '*', '+', '=', '<', '>', '{', '}', '!', '?', '\t'];

    private const int MinTokenLength = 2;

    private readonly string _root;
    private readonly object _rebuildGate = new();
    private volatile IndexData _data = IndexData.Empty;

    private KnowledgeIndex(string root) => _root = root;

    public static KnowledgeIndex ForRoot(string root) {
        var key = Path.GetFullPath(root);
        return Indexes.GetOrAdd(key, static path => new KnowledgeIndex(path));
    }

    /// <summary>Test seam: drop all cached indexes so a temp corpus is not reused across tests.</summary>
    internal static void ClearCaches() => Indexes.Clear();

    public IndexSnapshot GetOrBuild(List<string> warnings, CancellationToken cancellationToken) {
        var stamp = ComputeStamp(_root, cancellationToken);
        var current = _data;
        if (current.Stamp == stamp) {
            return current.ToSnapshot();
        }

        lock (_rebuildGate) {
            current = _data;
            if (current.Stamp == stamp) {
                return current.ToSnapshot();
            }

            _data = Build(_root, stamp, warnings, cancellationToken);
            return _data.ToSnapshot();
        }
    }

    private static IndexData Build(string root, string stamp, List<string> warnings, CancellationToken cancellationToken) {
        var docs = new List<IndexedDocument>();
        var postings = new Dictionary<string, List<Posting>>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in EnumerateMarkdown(root, cancellationToken)) {
            cancellationToken.ThrowIfCancellationRequested();
            var content = TryRead(file, warnings);
            if (content is null) {
                continue;
            }

            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            var tokens = TokenizeContent(content);
            // Filename and path segments participate in BM25 so a title match still
            // surfaces when the body never repeats the query term.
            tokens.AddRange(TokenizeContent(Path.GetFileNameWithoutExtension(file)));
            tokens.AddRange(TokenizeContent(relative.Replace('/', ' ').Replace('\\', ' ').Replace('.', ' ')));
            var tf = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var token in tokens) {
                tf[token] = tf.TryGetValue(token, out var count) ? count + 1 : 1;
            }

            var docId = docs.Count;
            docs.Add(new IndexedDocument(file, relative, content, tokens.Count, tf.Count));

            foreach (var (term, termTf) in tf) {
                if (!postings.TryGetValue(term, out var list)) {
                    list = [];
                    postings[term] = list;
                }

                list.Add(new Posting(docId, termTf));
            }
        }

        var avgDl = docs.Count == 0 ? 0.0 : docs.Average(d => d.Length);
        return new IndexData(stamp, docs, postings, avgDl);
    }

    public static double Bm25(
        IndexSnapshot index,
        IReadOnlyList<string> queryTokens,
        int docId) {
        if (queryTokens.Count == 0 || docId < 0 || docId >= index.Docs.Count) {
            return 0;
        }

        var doc = index.Docs[docId];
        var n = index.Docs.Count;
        var score = 0.0;
        foreach (var term in queryTokens) {
            if (!index.Postings.TryGetValue(term, out var list)) {
                continue;
            }

            var posting = FindPosting(list, docId);
            if (posting is null) {
                continue;
            }

            var df = list.Count;
            var idf = Math.Log((n - df + 0.5) / (df + 0.5) + 1.0);
            var tf = posting.Value.Tf;
            var denom = tf + K1 * (1 - B + B * doc.Length / Math.Max(index.AvgDl, 1.0));
            score += idf * (tf * (K1 + 1)) / denom;
        }

        return score;
    }

    private static Posting? FindPosting(List<Posting> list, int docId) {
        // Postings are appended in docId order during a single build.
        var lo = 0;
        var hi = list.Count - 1;
        while (lo <= hi) {
            var mid = lo + ((hi - lo) / 2);
            var p = list[mid];
            if (p.DocId == docId) {
                return p;
            }

            if (p.DocId < docId) {
                lo = mid + 1;
            } else {
                hi = mid - 1;
            }
        }

        return null;
    }

    internal static IReadOnlyList<string> TokenizeQuery(string query) {
        var tokens = new List<string>();
        foreach (var token in query.Split(
            TokenSeparators,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
            var lower = token.ToLowerInvariant();
            if (lower.Length >= MinTokenLength && !tokens.Contains(lower, StringComparer.OrdinalIgnoreCase)) {
                tokens.Add(lower);
            }
        }

        return tokens;
    }

    private static List<string> TokenizeContent(string content) {
        var tokens = new List<string>();
        foreach (var token in content.Split(
            TokenSeparators,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
            if (token.Length >= MinTokenLength) {
                tokens.Add(token.ToLowerInvariant());
            }
        }

        return tokens;
    }

    private static string ComputeStamp(string root, CancellationToken cancellationToken) {
        var sb = new StringBuilder();
        foreach (var file in EnumerateMarkdown(root, cancellationToken).OrderBy(f => f, StringComparer.OrdinalIgnoreCase)) {
            cancellationToken.ThrowIfCancellationRequested();
            try {
                var ticks = File.GetLastWriteTimeUtc(file).Ticks;
                sb.Append(file).Append('\0').Append(ticks).Append('\n');
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                sb.Append(file).Append("\0?\n");
            }
        }

        return sb.ToString();
    }

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

    private static string? TryRead(string file, List<string> warnings) {
        try {
            var info = new FileInfo(file);
            if (info.Exists && info.Length > KnowledgeSearchEngine.MaxFileCharacters) {
                warnings.Add($"Skipped oversized file '{file}' ({info.Length} bytes).");
                return null;
            }

            var content = File.ReadAllText(file);
            if (content.Length > KnowledgeSearchEngine.MaxFileCharacters) {
                warnings.Add($"Skipped oversized file '{file}' ({content.Length} characters).");
                return null;
            }

            return content;
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FileNotFoundException) {
            warnings.Add($"Skipped unreadable file '{file}': {ex.Message}");
            return null;
        }
    }

    internal readonly record struct Posting(int DocId, int Tf);

    internal sealed record IndexedDocument(
        string FilePath,
        string RelativePath,
        string Content,
        int Length,
        int UniqueTerms);

    internal sealed class IndexSnapshot {
        public required IReadOnlyList<IndexedDocument> Docs { get; init; }
        public required IReadOnlyDictionary<string, List<Posting>> Postings { get; init; }
        public required double AvgDl { get; init; }
        public required string Stamp { get; init; }
    }

    private sealed class IndexData {
        public static readonly IndexData Empty = new("", [], new Dictionary<string, List<Posting>>(StringComparer.OrdinalIgnoreCase), 0);

        public IndexData(
            string stamp,
            List<IndexedDocument> docs,
            Dictionary<string, List<Posting>> postings,
            double avgDl) {
            Stamp = stamp;
            Docs = docs;
            Postings = postings;
            AvgDl = avgDl;
        }

        public string Stamp { get; }
        public List<IndexedDocument> Docs { get; }
        public Dictionary<string, List<Posting>> Postings { get; }
        public double AvgDl { get; }

        public IndexSnapshot ToSnapshot() => new() {
            Docs = Docs,
            Postings = Postings,
            AvgDl = AvgDl,
            Stamp = Stamp
        };
    }
}
