using Microsoft.Extensions.Options;
using UiPath.Engineering.Mcp.Core.Configuration;
using UiPath.Engineering.Mcp.Providers.Filesystem;

namespace UiPath.Engineering.Mcp.Providers.Tests;

public class FilesystemProviderTests
{
    private static FilesystemProvider CreateSut(params string[] roots)
    {
        var options = Options.Create(new ProjectRootOptions
        {
            AllowedRoots = roots.ToList()
        });
        return new FilesystemProvider(options);
    }

    [Fact]
    public void IsPathAllowed_RootItself_IsAllowed()
    {
        var root = Path.Combine(Path.GetTempPath(), "mcp-root");
        var sut = CreateSut(root);

        Assert.True(sut.IsPathAllowed(root));
    }

    [Fact]
    public void IsPathAllowed_ChildPath_IsAllowed()
    {
        var root = Path.Combine(Path.GetTempPath(), "mcp-root");
        var child = Path.Combine(root, "projectA", "project.json");
        var sut = CreateSut(root);

        Assert.True(sut.IsPathAllowed(child));
    }

    [Fact]
    public void IsPathAllowed_SiblingWithSharedPrefix_IsRejected()
    {
        // Guard against the classic prefix bug: "mcp-root" must NOT allow "mcp-root-evil".
        var root = Path.Combine(Path.GetTempPath(), "mcp-root");
        var sibling = Path.Combine(Path.GetTempPath(), "mcp-root-evil", "project.json");
        var sut = CreateSut(root);

        Assert.False(sut.IsPathAllowed(sibling));
    }

    [Fact]
    public void IsPathAllowed_UnrelatedPath_IsRejected()
    {
        var root = Path.Combine(Path.GetTempPath(), "mcp-root");
        var sut = CreateSut(root);

        Assert.False(sut.IsPathAllowed(Path.Combine(Path.GetTempPath(), "somewhere-else")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void IsPathAllowed_EmptyOrWhitespace_IsRejected(string path)
    {
        var sut = CreateSut(Path.GetTempPath());

        Assert.False(sut.IsPathAllowed(path));
    }

    [Fact]
    public void IsPathAllowed_NoRootsConfigured_RejectsEverything()
    {
        var sut = CreateSut();

        Assert.False(sut.IsPathAllowed(Path.GetTempPath()));
    }

    [Fact]
    public void FindProjectJson_LocatesFileInDirectory()
    {
        using var temp = new TempDir();
        File.WriteAllText(Path.Combine(temp.Path, "project.json"), "{}");
        var sut = CreateSut(temp.Path);

        var found = sut.FindProjectJson(temp.Path);

        Assert.NotNull(found);
        Assert.EndsWith("project.json", found);
    }

    [Fact]
    public void FindProjectJson_ReturnsNullWhenMissing()
    {
        using var temp = new TempDir();
        var sut = CreateSut(temp.Path);

        Assert.Null(sut.FindProjectJson(temp.Path));
    }

    [Fact]
    public void FindXamlFiles_SkipsBinObjAndVcsFolders()
    {
        using var temp = new TempDir();

        // Real workflows.
        File.WriteAllText(Path.Combine(temp.Path, "Main.xaml"), "<x/>");
        var sub = Directory.CreateDirectory(Path.Combine(temp.Path, "Sub"));
        File.WriteAllText(Path.Combine(sub.FullName, "Process.xaml"), "<x/>");

        // Noise that must be ignored.
        var bin = Directory.CreateDirectory(Path.Combine(temp.Path, "bin"));
        File.WriteAllText(Path.Combine(bin.FullName, "junk.xaml"), "<x/>");
        var obj = Directory.CreateDirectory(Path.Combine(temp.Path, "obj"));
        File.WriteAllText(Path.Combine(obj.FullName, "junk2.xaml"), "<x/>");
        var git = Directory.CreateDirectory(Path.Combine(temp.Path, ".git"));
        File.WriteAllText(Path.Combine(git.FullName, "junk3.xaml"), "<x/>");

        var sut = CreateSut(temp.Path);

        var files = sut.FindXamlFiles(temp.Path).Select(Path.GetFileName).ToList();

        Assert.Equal(2, files.Count);
        Assert.Contains("Main.xaml", files);
        Assert.Contains("Process.xaml", files);
        Assert.DoesNotContain("junk.xaml", files);
        Assert.DoesNotContain("junk2.xaml", files);
        Assert.DoesNotContain("junk3.xaml", files);
    }

    [Fact]
    public void FindXamlFiles_NonExistentDirectory_ReturnsEmpty()
    {
        var sut = CreateSut(Path.GetTempPath());

        Assert.Empty(sut.FindXamlFiles(Path.Combine(Path.GetTempPath(), "does-not-exist-xyz")));
    }

    [Fact]
    public void GetDirectoryTree_RespectsMaxDepth()
    {
        using var temp = new TempDir();
        var level1 = Directory.CreateDirectory(Path.Combine(temp.Path, "Level1"));
        var level2 = Directory.CreateDirectory(Path.Combine(level1.FullName, "Level2"));
        var level3 = Directory.CreateDirectory(Path.Combine(level2.FullName, "Level3"));
        File.WriteAllText(Path.Combine(level3.FullName, "Deep.xaml"), "<x/>");
        var sut = CreateSut(temp.Path);

        var tree = sut.GetDirectoryTree(temp.Path, maxDepth: 2);

        Assert.True(tree.IsDirectory);
        var l1 = Assert.Single(tree.Children, c => c.Name == "Level1");
        var l2 = Assert.Single(l1.Children, c => c.Name == "Level2");
        Assert.Empty(l2.Children); // depth limit reached, Level3 not enumerated
    }

    [Fact]
    public void GetDirectoryTree_SkipsIgnoredDirectoriesAndIncludesFiles()
    {
        using var temp = new TempDir();
        File.WriteAllText(Path.Combine(temp.Path, "Main.xaml"), "<x/>");
        Directory.CreateDirectory(Path.Combine(temp.Path, "Sub"));
        var bin = Directory.CreateDirectory(Path.Combine(temp.Path, "bin"));
        File.WriteAllText(Path.Combine(bin.FullName, "junk.xaml"), "<x/>");
        var git = Directory.CreateDirectory(Path.Combine(temp.Path, ".git"));
        File.WriteAllText(Path.Combine(git.FullName, "junk2.xaml"), "<x/>");
        var sut = CreateSut(temp.Path);

        var tree = sut.GetDirectoryTree(temp.Path);

        var names = tree.Children.Select(c => c.Name).ToList();
        Assert.Contains("Main.xaml", names);
        Assert.Contains("Sub", names);
        Assert.DoesNotContain("bin", names);
        Assert.DoesNotContain(".git", names);
        Assert.False(tree.Children.Single(c => c.Name == "Main.xaml").IsDirectory);
        Assert.True(tree.Children.Single(c => c.Name == "Sub").IsDirectory);
    }

    [Fact]
    public void GetDirectoryTree_NonExistentDirectory_ReturnsEmptyRootNode()
    {
        var sut = CreateSut(Path.GetTempPath());
        var missing = Path.Combine(Path.GetTempPath(), "does-not-exist-xyz");

        var tree = sut.GetDirectoryTree(missing);

        Assert.Equal(missing, tree.Path);
        Assert.True(tree.IsDirectory);
        Assert.Empty(tree.Children);
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mcp-tests-" + Guid.NewGuid().ToString("N"));

        public TempDir() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch { /* best effort */ }
        }
    }
}
