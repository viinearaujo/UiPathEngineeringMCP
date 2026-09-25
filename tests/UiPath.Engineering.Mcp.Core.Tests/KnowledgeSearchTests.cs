using UiPath.Engineering.Mcp.Core.Configuration;
using UiPath.Engineering.Mcp.Core.Knowledge;

namespace UiPath.Engineering.Mcp.Core.Tests;

/// <summary>
/// Exercises the excerpt engine against a real on-disk corpus in a temp folder, plus the corpus
/// resolver's relative-path walking. The engine is what replaces a whole-file read, so the
/// assertions are about the excerpt (its file, line, and heading) rather than a match count.
/// </summary>
public class KnowledgeSearchTests : IDisposable {
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mcp-know-" + Guid.NewGuid().ToString("N"));

    public KnowledgeSearchTests() {
        KnowledgeIndex.ClearCaches();
        Directory.CreateDirectory(_root);
    }

    public void Dispose() {
        KnowledgeIndex.ClearCaches();
        if (Directory.Exists(_root)) {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string Write(string relative, string content) {
        var path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        // Nudge mtime so the index stamp cannot collide with a prior write in the same tick.
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMilliseconds(1));
        return path;
    }

    [Fact]
    public void Search_ReturnsExcerptWithLineAndNearestHeading() {
        Write("UiPath.System.Activities/26.4/activities/RetryScope.md",
            """
            # Retry Scope

            ## Properties

            ### Input

            | Property | Type |
            |----------|------|
            | NumberOfRetries | int |
            | RetryInterval | TimeSpan |

            ## Notes

            The retry interval must be under five minutes.
            """);

        var result = KnowledgeSearchEngine.Search(
            [new KnowledgeCorpus(_root, KnowledgeSource.Vendored)], "RetryInterval");

        var excerpt = Assert.Single(result.Excerpts);
        Assert.Equal(10, excerpt.Line);
        Assert.Equal("Input", excerpt.Heading);
        Assert.Contains("RetryInterval", excerpt.Snippet);
        Assert.Equal("UiPath.System.Activities", excerpt.Package);
        Assert.Equal("RetryScope", excerpt.Activity);
        Assert.Equal("vendored", excerpt.Source);
        Assert.Equal(1, result.FilesSearched);
    }

    [Fact]
    public void Search_ExactCaseOutranksCaseInsensitive() {
        Write("a/exact.md", "# Doc\nalpha token = 1\n");
        Write("b/insensitive.md", "# Doc\nALPHA TOKEN = 2\n");

        var result = KnowledgeSearchEngine.Search(
            [new KnowledgeCorpus(_root, KnowledgeSource.Vendored)], "alpha token");

        Assert.Equal(2, result.Excerpts.Count);
        Assert.Equal("a/exact.md", result.Excerpts[0].RelativePath);
        Assert.True(result.Excerpts[0].Score > result.Excerpts[1].Score);
    }

    [Fact]
    public void Search_ExactFileNameMatchIsRankedFirst() {
        Write("guides/selector-guide.md", "# Selector Guide\nUnrelated body text.\n");
        Write("guides/other.md", "# Other\nThe selector rules live here.\n");

        var result = KnowledgeSearchEngine.Search(
            [new KnowledgeCorpus(_root, KnowledgeSource.Vendored)], "selector");

        Assert.Equal("guides/selector-guide.md", result.Excerpts[0].RelativePath);
    }

    [Fact]
    public void Search_RareExactTermOutranksCommonTerm() {
        // BM25 property: a rare term in one document outranks a common term that
        // appears in many documents. Filename matches still surface via path tokens.
        Write("rare/unique-zephyr.md", "# Zephyr\nThe zephyr widget configures throttling.\n");
        for (var i = 0; i < 8; i++) {
            Write($"common/doc{i}.md", $"# Doc {i}\nCommon glue text about workflows and activities.\n");
        }

        var rare = KnowledgeSearchEngine.Search(
            [new KnowledgeCorpus(_root, KnowledgeSource.Vendored)], "zephyr");
        Assert.NotEmpty(rare.Excerpts);
        Assert.All(rare.Excerpts, e => Assert.Equal("rare/unique-zephyr.md", e.RelativePath));
        Assert.True(rare.Excerpts.Count <= KnowledgeSearchEngine.MaxPerFile);

        var common = KnowledgeSearchEngine.Search(
            [new KnowledgeCorpus(_root, KnowledgeSource.Vendored)], "glue");
        Assert.NotEmpty(common.Excerpts);
        // Every common hit scores below the rare-term hit (lower IDF).
        Assert.True(rare.Excerpts[0].Score > common.Excerpts[0].Score);
    }

    [Fact]
    public void Search_CapsExcerptsPerFile() {
        Write("many.md", "# Many\n" + string.Join('\n', Enumerable.Repeat("needle here", 20)));

        var result = KnowledgeSearchEngine.Search(
            [new KnowledgeCorpus(_root, KnowledgeSource.Vendored)], "needle", maxResults: 10);

        Assert.Equal(KnowledgeSearchEngine.MaxPerFile, result.Excerpts.Count);
    }

    [Fact]
    public void Search_RespectsMaxResultsAndFlagsTruncation() {
        for (var i = 0; i < 6; i++) {
            Write($"doc{i}.md", $"# Doc {i}\nneedle {i}\n");
        }

        var result = KnowledgeSearchEngine.Search(
            [new KnowledgeCorpus(_root, KnowledgeSource.Vendored)], "needle", maxResults: 3);

        Assert.Equal(3, result.Excerpts.Count);
        Assert.True(result.Truncated);
        Assert.NotNull(result.Note);
    }

    [Fact]
    public void Search_PackageFilterKeepsOnlyThatPathSegment() {
        Write("UiPath.Excel.Activities/24.10/activities/ReadRange.md", "# Read Range\nneedle\n");
        Write("UiPath.System.Activities/26.4/activities/LogMessage.md", "# Log Message\nneedle\n");

        var result = KnowledgeSearchEngine.Search(
            [new KnowledgeCorpus(_root, KnowledgeSource.Vendored)], "needle",
            maxResults: 10, packageFilter: "UiPath.Excel.Activities");

        var excerpt = Assert.Single(result.Excerpts);
        Assert.Equal("UiPath.Excel.Activities", excerpt.Package);
    }

    [Fact]
    public void Search_MissingCorpusIsWarnedNotThrown() {
        var result = KnowledgeSearchEngine.Search(
            [new KnowledgeCorpus(Path.Combine(_root, "nope"), KnowledgeSource.ProjectLocal)], "anything");

        Assert.Empty(result.Excerpts);
        Assert.Contains(result.Warnings, w => w.Contains("does not exist", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Search_EmptyQueryIsAWarningNotAResult() {
        var result = KnowledgeSearchEngine.Search(
            [new KnowledgeCorpus(_root, KnowledgeSource.Vendored)], "   ");

        Assert.Empty(result.Excerpts);
        Assert.Contains("query is required.", result.Warnings);
    }

    [Fact]
    public void Search_TitleOnlyMatchStillReturnsAHeadExcerpt() {
        Write("guides/wrapped.md", "# Troubleshooting Wrappers\n\nThe generated shell fails when the target method is missing.\n");

        var result = KnowledgeSearchEngine.Search(
            [new KnowledgeCorpus(_root, KnowledgeSource.Vendored)], "wrappers");

        var excerpt = Assert.Single(result.Excerpts);
        Assert.Equal(1, excerpt.Line);
        Assert.Contains("Troubleshooting Wrappers", excerpt.Snippet);
    }

    [Fact]
    public void Search_ReindexesWhenMarkdownMtimeChanges() {
        Write("a.md", "# First\noldterm appears here\n");
        var first = KnowledgeSearchEngine.Search(
            [new KnowledgeCorpus(_root, KnowledgeSource.Vendored)], "oldterm");
        Assert.Single(first.Excerpts);

        Write("a.md", "# First\nnewterm appears here\n");
        var second = KnowledgeSearchEngine.Search(
            [new KnowledgeCorpus(_root, KnowledgeSource.Vendored)], "newterm");
        Assert.Single(second.Excerpts);
        Assert.Empty(KnowledgeSearchEngine.Search(
            [new KnowledgeCorpus(_root, KnowledgeSource.Vendored)], "oldterm").Excerpts);
    }

    [Fact]
    public void ResolveUnderSkillsRoot_FindsRelativePathByWalkingUp() {
        Write("skills/uipath-rpa/references/activity-docs/overview.md", "# Overview\n");
        var options = new SkillsOptions { SkillsRoot = "skills" };

        var resolved = KnowledgePaths.ResolveActivityDocsRoot(options, _root);

        Assert.NotNull(resolved);
        Assert.True(Directory.Exists(resolved));
        Assert.EndsWith("activity-docs", resolved);
    }

    [Fact]
    public void ResolveUnderSkillsRoot_ReturnsNullWhenAbsent() {
        Assert.Null(KnowledgePaths.ResolveActivityDocsRoot(new SkillsOptions { SkillsRoot = "absent" }, _root));
    }

    [Fact]
    public void ResolveProjectActivityDocsRoot_ReturnsNullForMissingProject() {
        Assert.Null(KnowledgePaths.ResolveProjectActivityDocsRoot(Path.Combine(_root, "no-project")));
        Assert.Null(KnowledgePaths.ResolveProjectActivityDocsRoot(null));
    }

    [Fact]
    public void ResolveProjectActivityDocsRoot_FindsTheLocalTree() {
        Directory.CreateDirectory(Path.Combine(_root, ".local", "docs", "packages", "UiPath.System.Activities"));

        var resolved = KnowledgePaths.ResolveProjectActivityDocsRoot(_root);

        Assert.NotNull(resolved);
        Assert.EndsWith(Path.Combine("docs", "packages"), resolved);
    }
}
