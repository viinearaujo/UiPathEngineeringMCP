using UiPath.Engineering.Mcp.Providers.Git;

namespace UiPath.Engineering.Mcp.Providers.Tests;

public class GitProviderTests {
    [Fact]
    public void BuildInvocation_PutsDashCAndRepoPathAsSeparateTokens() {
        var path = @"C:\repos\my project & notes";

        var args = GitProvider.BuildInvocation(path, "status", "--porcelain=v1", "--branch");

        Assert.Equal(["-C", path, "status", "--porcelain=v1", "--branch"], args);
        Assert.DoesNotContain(args, a => a.Contains("-C \"", StringComparison.Ordinal));
        Assert.DoesNotContain(args, a => a.Contains("\"C:\\", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildInvocation_LogArgs_KeepPrettyFormatAsOneToken() {
        var format = "--pretty=format:%H%x1f%an%x1f%aI%x1f%s";

        var args = GitProvider.BuildInvocation(@"C:\repo", "log", "-n", "10", format);

        Assert.Equal(["-C", @"C:\repo", "log", "-n", "10", format], args);
    }

    [Fact]
    public void FormatGitErrors_RedactsStderrSecrets() {
        var errors = GitProvider.FormatGitErrors("fatal: token=abc123secret was rejected");

        var line = Assert.Single(errors);
        Assert.StartsWith("[git] ", line);
        Assert.DoesNotContain("abc123secret", line);
        Assert.Contains("***REDACTED***", line);
    }

    [Fact]
    public async Task GetStatusAsync_PathContainingAmpersand_IsPassedAsSingleArgument() {
        var tempDir = Path.Combine(Path.GetTempPath(), "not-a-repo-" + Guid.NewGuid().ToString("N") + " & calc");
        Directory.CreateDirectory(tempDir);
        try {
            var sut = new GitProvider(new FakeFilesystemProviderForGit());

            var result = await sut.GetStatusAsync(tempDir);

            Assert.False(result.IsRepository);
            Assert.Contains(result.Errors, e => e.Contains("not a git repository", StringComparison.OrdinalIgnoreCase));
        } finally {
            Directory.Delete(tempDir, recursive: true);
        }
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
        public void DeleteFile(string filePath) { }
        public bool FileExists(string path) => false;
    }
}
