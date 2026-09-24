using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using UiPath.Engineering.Mcp.Core;
using UiPath.Engineering.Mcp.Core.Configuration;
using UiPath.Engineering.Mcp.Core.Knowledge;
using UiPath.Engineering.Mcp.Core.Models;

namespace UiPath.Engineering.Mcp.Tools;

/// <summary>
/// Excerpt search across the whole RPA knowledge corpus: the activity docs plus the guide
/// references (XAML rules, coded guides, UIA, debugging, testing, cli-reference, Common
/// Pitfalls). This is the alternative to <c>read_skill</c>'s one-exact-path-at-a-time read —
/// it returns the matching excerpt with its file and location, so a caller does not have to
/// open the whole document to find one rule.
/// </summary>
[McpServerToolType]
public sealed class SearchUiPathKnowledgeTool {
    private const int DefaultMaxResults = 12;

    private readonly SkillsOptions _skills;

    public SearchUiPathKnowledgeTool(IOptions<SkillsOptions> skills) => _skills = skills.Value;

    // The automatic snake_case name would be "search_ui_path_knowledge" (it splits the
    // PascalCase run), so the tool name is pinned to keep "uipath" one word.
    [McpServerTool(Name = "search_uipath_knowledge", UseStructuredContent = true), Description("Searches the UiPath RPA knowledge base — the activity documentation plus the guide corpus (XAML basics and rules, coded vs XAML, UI Automation, debugging, testing, REFramework, error handling, Common Pitfalls, the CLI reference) — and returns matching EXCERPTS with their file, line, and nearest heading. Use this instead of read_skill when you need one rule, one pattern, or one troubleshooting note: read_skill reads a whole file, this returns just the matching excerpt. Pass kind='guides' or kind='activityDocs' to narrow the corpus. Next: read_skill for the full document, or get_activity_metadata for one activity's surface.")]
    public ToolResult SearchUiPathKnowledge(
        [Description("What to look for, e.g. 'JIT compilation is disabled', 'Rule 24 Sequence wrap', 'selector', 'retry scope', or 'invoke coded workflow'.")] string query,
        [Description("Which corpus to search: all (default), guides (the reference guides), or activityDocs (per-activity documentation).")]
        [AllowedValues(AllKind, GuidesKind, ActivityDocsKind)] string kind = AllKind,
        [Description("Maximum excerpts to return (default 12, max 50).")] int? maxResults = null,
        CancellationToken cancellationToken = default) {

        var sw = Stopwatch.StartNew();

        if (!string.IsNullOrWhiteSpace(kind)
            && ToolArgs.ParseChoice(kind, "kind", Kinds, sw, out var parsedKind) is { } kindError) {
            return kindError;
        }

        var corpora = BuildCorpora(kind);
        if (corpora.Count == 0) {
            return ToolResults.Failure(new ToolError(
                ToolErrorCodes.SkillsRootMissing,
                "The skills knowledge corpus was not found.",
                "Set Skills:SkillsRoot in appsettings.json to the directory containing uipath-rpa/ (default '.agents/skills')."), sw);
        }

        var result = KnowledgeSearchEngine.Search(corpora, query, maxResults ?? DefaultMaxResults, packageFilter: null, cancellationToken);

        var warnings = new List<string>(result.Warnings);
        if (result.Excerpts.Count == 0) {
            warnings.Add($"No knowledge base content matched '{query}'. Try a shorter or more specific term; read_skill('<skill>', '<file>') remains available when you know the path.");
        }

        return ToolResults.Ok(
            result.Excerpts.Count == 0
                ? $"No knowledge base content matched '{query}'."
                : $"{result.Excerpts.Count} excerpt(s) for '{query}' across {result.FilesSearched} file(s).",
            new {
                query,
                kind,
                sourcesSearched = result.RootsSearched,
                filesSearched = result.FilesSearched,
                excerpts = result.Excerpts.Select(SearchActivityDocsTool.ToPayload).ToList(),
                note = result.Note
            }, sw, warnings);
    }

    internal const string AllKind = "all";
    internal const string GuidesKind = "guides";
    internal const string ActivityDocsKind = "activityDocs";
    internal static readonly string[] Kinds = [AllKind, GuidesKind, ActivityDocsKind];

    private List<KnowledgeCorpus> BuildCorpora(string kind) {
        var corpora = new List<KnowledgeCorpus>();
        var wantsGuides = !string.Equals(kind, ActivityDocsKind, StringComparison.OrdinalIgnoreCase);
        var wantsActivityDocs = !string.Equals(kind, GuidesKind, StringComparison.OrdinalIgnoreCase);

        // The guides root is the parent of activity-docs, so it already contains the activity
        // docs; adding both would double-report every activity file. Add the deeper root only
        // when the activity docs were asked for on their own.
        if (wantsGuides && KnowledgePaths.ResolveReferenceGuidesRoot(_skills) is { } guides) {
            corpora.Add(new KnowledgeCorpus(guides, KnowledgeSource.Vendored));
        } else if (wantsActivityDocs && KnowledgePaths.ResolveActivityDocsRoot(_skills) is { } activityDocs) {
            corpora.Add(new KnowledgeCorpus(activityDocs, KnowledgeSource.Vendored));
        }

        return corpora;
    }
}