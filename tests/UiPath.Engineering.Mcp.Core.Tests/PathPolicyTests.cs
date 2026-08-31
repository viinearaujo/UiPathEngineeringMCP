using UiPath.Engineering.Mcp.Core;
using UiPath.Engineering.Mcp.Core.Configuration;

namespace UiPath.Engineering.Mcp.Core.Tests;

public class PathPolicyTests {
    [Fact]
    public void IsAllowed_RootItself_IsAllowed() {
        var root = Path.Combine(Path.GetTempPath(), "mcp-policy-root");
        var sut = new PathPolicy([root]);

        Assert.True(sut.IsAllowed(root));
        Assert.Equal(Path.GetFullPath(root), sut.EnsureAllowed(root));
    }

    [Fact]
    public void IsAllowed_ChildPath_IsAllowed() {
        var root = Path.Combine(Path.GetTempPath(), "mcp-policy-root");
        var child = Path.Combine(root, "projectA", "project.json");
        var sut = new PathPolicy([root]);

        Assert.True(sut.IsAllowed(child));
    }

    [Fact]
    public void IsAllowed_SiblingWithSharedPrefix_IsRejected() {
        var root = Path.Combine(Path.GetTempPath(), "mcp-policy-root");
        var sibling = Path.Combine(Path.GetTempPath(), "mcp-policy-root-evil", "project.json");
        var sut = new PathPolicy([root]);

        Assert.False(sut.IsAllowed(sibling));
    }

    [Fact]
    public void IsAllowed_NoRootsConfigured_RejectsEverything() {
        var sut = new PathPolicy([]);

        Assert.False(sut.IsAllowed(Path.GetTempPath()));
        Assert.Throws<UnauthorizedAccessException>(() => sut.EnsureAllowed(Path.GetTempPath()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void IsAllowed_EmptyOrWhitespace_IsRejected(string path) {
        var sut = new PathPolicy([Path.GetTempPath()]);

        Assert.False(sut.IsAllowed(path));
    }

    [Theory]
    [InlineData(".env")]
    [InlineData(".env.local")]
    [InlineData("orchestrator-credentials.json")]
    [InlineData("server.pem")]
    [InlineData("certs/id_rsa.KEY")]
    public void IsSecretName_BlockedNames_AreDetected(string relativePath) {
        var sut = new PathPolicy([]);

        Assert.True(sut.IsSecretName(relativePath));
        Assert.True(PathPolicy.LooksLikeSecret(relativePath));
        Assert.True(ProjectFilePolicy.IsSecretName(relativePath));
    }

    [Fact]
    public void IsSecretName_OrdinaryProjectFile_IsAllowed() {
        var sut = new PathPolicy([]);

        Assert.False(sut.IsSecretName("Main.xaml"));
        Assert.False(sut.IsSecretName("docs/notes.md"));
    }

    [Fact]
    public void TryResolveWithinProject_AcceptsChildAndRejectsEscape() {
        var sut = new PathPolicy([]);

        Assert.True(sut.TryResolveWithinProject("/projects/p", "Main.xaml", out var inside));
        Assert.False(string.IsNullOrWhiteSpace(inside));
        Assert.False(sut.TryResolveWithinProject("/projects/p", "../evil.xaml", out _));
        Assert.False(sut.TryResolveWithinProject("/projects/p", "", out _));
    }

    [Fact]
    public void ExceedsMaxSize_UsesSharedByteCap() {
        var sut = new PathPolicy([]);

        Assert.False(sut.ExceedsMaxSize(FileReadLimits.MaxFileBytes));
        Assert.True(sut.ExceedsMaxSize(FileReadLimits.MaxFileBytes + 1L));
    }

    [Fact]
    public void EnsureAllowed_ReparsePointEscapingRoot_IsRejected() {
        using var temp = new TempDir();
        var root = Path.Combine(temp.Path, "root");
        var outside = Path.Combine(temp.Path, "outside");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outside);
        var secret = Path.Combine(outside, "secret.txt");
        File.WriteAllText(secret, "leaked");

        var link = Path.Combine(root, "escape");
        if (!TryCreateDirectoryLink(link, outside)) {
            return;
        }

        var sut = new PathPolicy([root]);
        Assert.False(sut.IsAllowed(Path.Combine(link, "secret.txt")));
        Assert.Throws<UnauthorizedAccessException>(() => sut.EnsureAllowed(Path.Combine(link, "secret.txt")));
    }

    [Fact]
    public void IsAllowed_DanglingLinkPointingOutside_IsRejected() {
        using var temp = new TempDir();
        var root = Path.Combine(temp.Path, "root");
        Directory.CreateDirectory(root);
        var outsideTarget = Path.Combine(temp.Path, "missing-target-" + Guid.NewGuid().ToString("N"));
        var link = Path.Combine(root, "dangling");
        if (!TryCreateFileLink(link, outsideTarget)) {
            return;
        }

        var sut = new PathPolicy([root]);
        Assert.False(sut.IsAllowed(link));
    }

    [Fact]
    public void ProjectRootOptions_Constructor_ReadsAllowedRoots() {
        var root = Path.Combine(Path.GetTempPath(), "mcp-options-root");
        var sut = new PathPolicy(new ProjectRootOptions { AllowedRoots = [root] });

        Assert.True(sut.IsAllowed(root));
    }

    private static bool TryCreateDirectoryLink(string linkPath, string targetPath) {
        try {
            Directory.CreateSymbolicLink(linkPath, targetPath);
            return Directory.Exists(linkPath);
        } catch (Exception ex) when (
            ex is IOException
            or UnauthorizedAccessException
            or PlatformNotSupportedException) {
            return TryCreateJunction(linkPath, targetPath);
        }
    }

    private static bool TryCreateFileLink(string linkPath, string targetPath) {
        try {
            File.CreateSymbolicLink(linkPath, targetPath);
            return true;
        } catch (Exception ex) when (
            ex is IOException
            or UnauthorizedAccessException
            or PlatformNotSupportedException) {
            return false;
        }
    }

    private static bool TryCreateJunction(string linkPath, string targetPath) {
        if (!OperatingSystem.IsWindows()) {
            return false;
        }

        try {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo {
                FileName = "cmd.exe",
                ArgumentList = { "/c", "mklink", "/J", linkPath, targetPath },
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            if (process is null) {
                return false;
            }

            process.WaitForExit(5_000);
            return process.ExitCode == 0 && Directory.Exists(linkPath);
        } catch {
            return false;
        }
    }

    private sealed class TempDir : IDisposable {
        public string Path { get; } =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mcp-policy-" + Guid.NewGuid().ToString("N"));

        public TempDir() => Directory.CreateDirectory(Path);

        public void Dispose() {
            try { Directory.Delete(Path, recursive: true); } catch { /* best effort */ }
        }
    }
}
