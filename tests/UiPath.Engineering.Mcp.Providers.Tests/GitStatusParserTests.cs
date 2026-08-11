using UiPath.Engineering.Mcp.Providers.Git;

namespace UiPath.Engineering.Mcp.Providers.Tests;

public class GitStatusParserTests {
    private const string Repo = "/repos/sample";

    [Fact]
    public void Parse_CleanWorkingTree_ReturnsBranchAndNoChanges() {
        var output = "## main...origin/main\n";

        var result = GitStatusParser.Parse(Repo, output);

        Assert.True(result.IsRepository);
        Assert.Equal("main", result.Branch);
        Assert.Equal(0, result.AheadCount);
        Assert.Equal(0, result.BehindCount);
        Assert.Empty(result.ChangedFiles);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_ModifiedAndUntrackedFiles_ListsChangedFiles() {
        var output = "## main...origin/main\n M src/Workflow.xaml\n?? docs/notes.md\n";

        var result = GitStatusParser.Parse(Repo, output);

        Assert.Equal(2, result.ChangedFiles.Count);
        Assert.Contains("src/Workflow.xaml", result.ChangedFiles);
        Assert.Contains("docs/notes.md", result.ChangedFiles);
    }

    [Fact]
    public void Parse_AheadAndBehind_ParsesCounts() {
        var output = "## feature/x...origin/feature/x [ahead 2, behind 3]\n";

        var result = GitStatusParser.Parse(Repo, output);

        Assert.Equal("feature/x", result.Branch);
        Assert.Equal(2, result.AheadCount);
        Assert.Equal(3, result.BehindCount);
    }

    [Fact]
    public void Parse_AheadOnly_ParsesAheadWithoutBehind() {
        var output = "## main...origin/main [ahead 1]\n";

        var result = GitStatusParser.Parse(Repo, output);

        Assert.Equal(1, result.AheadCount);
        Assert.Equal(0, result.BehindCount);
    }

    [Fact]
    public void Parse_NoCommitsYet_ReturnsBranchWithoutTracking() {
        var output = "## No commits yet on main\n?? new-file.txt\n";

        var result = GitStatusParser.Parse(Repo, output);

        Assert.Equal("main", result.Branch);
        Assert.Contains("new-file.txt", result.ChangedFiles);
    }

    [Fact]
    public void Parse_RenamedFile_KeepsDestinationPath() {
        var output = "## main\nR  old.xaml -> new.xaml\n";

        var result = GitStatusParser.Parse(Repo, output);

        Assert.Single(result.ChangedFiles);
        Assert.Equal("new.xaml", result.ChangedFiles[0]);
    }

    [Fact]
    public void Parse_EmptyOutput_ReturnsIsolatedDefaults() {
        var result = GitStatusParser.Parse(Repo, string.Empty);

        Assert.True(result.IsRepository);
        Assert.Equal(string.Empty, result.Branch);
        Assert.Empty(result.ChangedFiles);
    }

    [Fact]
    public async Task GetStatusAsync_NotARepository_ReturnsIsRepositoryFalseWithoutThrowing() {
        // Real git against a temp directory that is not a repository.
        var tempDir = Path.Combine(Path.GetTempPath(), "not-a-repo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try {
            var fs = new FakeFilesystemProviderForGit();
            var sut = new GitProvider(fs);

            var result = await sut.GetStatusAsync(tempDir);

            Assert.False(result.IsRepository);
            Assert.NotEmpty(result.Errors);
            Assert.Contains(result.Errors, e => e.Contains("not a git repository", StringComparison.OrdinalIgnoreCase));
        } finally {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task GetStatusAsync_PathNotAllowed_ReturnsErrorWithoutRunningGit() {
        var fs = new FakeFilesystemProviderForGit { Allowed = false };
        var sut = new GitProvider(fs);

        var result = await sut.GetStatusAsync("/anywhere");

        Assert.False(result.IsRepository);
        Assert.Contains(result.Errors, e => e.Contains("allowed roots", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class FakeFilesystemProviderForGit : UiPath.Engineering.Mcp.Core.Abstractions.IFilesystemProvider {
        public bool Allowed { get; set; } = true;
        public bool IsPathAllowed(string requestedPath) => Allowed;
        public string? FindProjectJson(string projectPath) => null;
        public IReadOnlyList<string> FindXamlFiles(string projectPath) => [];
        public IReadOnlyList<string> FindCSharpFiles(string projectPath) => [];
        public string ReadAllText(string filePath) => string.Empty;
        public long GetFileSize(string filePath) => 0;
        public DateTime GetLastWriteTimeUtc(string filePath) => DateTime.UnixEpoch;
        public UiPath.Engineering.Mcp.Core.Models.DirectoryTreeNode GetDirectoryTree(string root, int maxDepth = 3) => new();
        public void CreateDirectory(string path) { }
        public void WriteAllText(string filePath, string content) { }
        public bool FileExists(string path) => false;
    }
}
