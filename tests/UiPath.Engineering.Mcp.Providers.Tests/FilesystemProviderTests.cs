using UiPath.Engineering.Mcp.Core;
using UiPath.Engineering.Mcp.Providers.Filesystem;

namespace UiPath.Engineering.Mcp.Providers.Tests;

public class FilesystemProviderTests {
    private static FilesystemProvider CreateSut(params string[] roots) =>
        new(new PathPolicy(roots));

    [Fact]
    public void IsPathAllowed_RootItself_IsAllowed() {
        var root = Path.Combine(Path.GetTempPath(), "mcp-root");
        var sut = CreateSut(root);

        Assert.True(sut.IsPathAllowed(root));
    }

    [Fact]
    public void IsPathAllowed_ChildPath_IsAllowed() {
        var root = Path.Combine(Path.GetTempPath(), "mcp-root");
        var child = Path.Combine(root, "projectA", "project.json");
        var sut = CreateSut(root);

        Assert.True(sut.IsPathAllowed(child));
    }

    [Fact]
    public void IsPathAllowed_SiblingWithSharedPrefix_IsRejected() {
        // Guard against the classic prefix bug: "mcp-root" must NOT allow "mcp-root-evil".
        var root = Path.Combine(Path.GetTempPath(), "mcp-root");
        var sibling = Path.Combine(Path.GetTempPath(), "mcp-root-evil", "project.json");
        var sut = CreateSut(root);

        Assert.False(sut.IsPathAllowed(sibling));
    }

    [Fact]
    public void IsPathAllowed_UnrelatedPath_IsRejected() {
        var root = Path.Combine(Path.GetTempPath(), "mcp-root");
        var sut = CreateSut(root);

        Assert.False(sut.IsPathAllowed(Path.Combine(Path.GetTempPath(), "somewhere-else")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void IsPathAllowed_EmptyOrWhitespace_IsRejected(string path) {
        var sut = CreateSut(Path.GetTempPath());

        Assert.False(sut.IsPathAllowed(path));
    }

    [Fact]
    public void IsPathAllowed_NoRootsConfigured_RejectsEverything() {
        var sut = CreateSut();

        Assert.False(sut.IsPathAllowed(Path.GetTempPath()));
    }

    [Fact]
    public void FindProjectJson_LocatesFileInDirectory() {
        using var temp = new TempDir();
        File.WriteAllText(Path.Combine(temp.Path, "project.json"), "{}");
        var sut = CreateSut(temp.Path);

        var found = sut.FindProjectJson(temp.Path);

        Assert.NotNull(found);
        Assert.EndsWith("project.json", found);
    }

    [Fact]
    public void FindProjectJson_ReturnsNullWhenMissing() {
        using var temp = new TempDir();
        var sut = CreateSut(temp.Path);

        Assert.Null(sut.FindProjectJson(temp.Path));
    }

    [Fact]
    public void FindXamlFiles_SkipsBinObjAndVcsFolders() {
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
    public void FindXamlFiles_NonExistentDirectory_ReturnsEmpty() {
        var sut = CreateSut(Path.GetTempPath());

        Assert.Empty(sut.FindXamlFiles(Path.Combine(Path.GetTempPath(), "does-not-exist-xyz")));
    }

    [Fact]
    public void FindCSharpFiles_SkipsBinObjAndVcsFolders() {
        using var temp = new TempDir();

        // Real coded workflows / source files.
        File.WriteAllText(Path.Combine(temp.Path, "InvoiceFlow.cs"), "// code");
        var sub = Directory.CreateDirectory(Path.Combine(temp.Path, "Sub"));
        File.WriteAllText(Path.Combine(sub.FullName, "Helpers.cs"), "// code");

        // Noise that must be ignored.
        var obj = Directory.CreateDirectory(Path.Combine(temp.Path, "obj"));
        File.WriteAllText(Path.Combine(obj.FullName, "Generated.cs"), "// code");
        var git = Directory.CreateDirectory(Path.Combine(temp.Path, ".git"));
        File.WriteAllText(Path.Combine(git.FullName, "junk.cs"), "// code");

        var sut = CreateSut(temp.Path);

        var files = sut.FindCSharpFiles(temp.Path).Select(Path.GetFileName).ToList();

        Assert.Equal(2, files.Count);
        Assert.Contains("InvoiceFlow.cs", files);
        Assert.Contains("Helpers.cs", files);
        Assert.DoesNotContain("Generated.cs", files);
        Assert.DoesNotContain("junk.cs", files);
    }

    [Fact]
    public void FindCSharpFiles_NonExistentDirectory_ReturnsEmpty() {
        var sut = CreateSut(Path.GetTempPath());

        Assert.Empty(sut.FindCSharpFiles(Path.Combine(Path.GetTempPath(), "does-not-exist-xyz")));
    }

    [Fact]
    public void GetDirectoryTree_RespectsMaxDepth() {
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
    public void GetDirectoryTree_SkipsIgnoredDirectoriesAndIncludesFiles() {
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
    public void GetDirectoryTree_NonExistentDirectory_ReturnsEmptyRootNode() {
        var sut = CreateSut(Path.GetTempPath());
        var missing = Path.Combine(Path.GetTempPath(), "does-not-exist-xyz");

        var tree = sut.GetDirectoryTree(missing);

        Assert.Equal(missing, tree.Path);
        Assert.True(tree.IsDirectory);
        Assert.Empty(tree.Children);
    }

    [Fact]
    public void WriteAllText_InsideAllowedRoot_WritesFile() {
        using var temp = new TempDir();
        var sut = CreateSut(temp.Path);
        var target = Path.Combine(temp.Path, "Main.xaml");

        sut.WriteAllText(target, "<x/>");

        Assert.Equal("<x/>", File.ReadAllText(target));
        Assert.True(sut.FileExists(target));
    }

    [Fact]
    public void WriteAllText_OutsideAllowedRoot_Throws() {
        using var temp = new TempDir();
        var sut = CreateSut(temp.Path);
        var outside = Path.Combine(Path.GetTempPath(), "outside-" + Guid.NewGuid().ToString("N"), "Main.xaml");

        Assert.Throws<UnauthorizedAccessException>(() => sut.WriteAllText(outside, "<x/>"));
    }

    [Fact]
    public void CreateDirectory_OutsideAllowedRoot_Throws() {
        using var temp = new TempDir();
        var sut = CreateSut(temp.Path);
        var outside = Path.Combine(Path.GetTempPath(), "outside-" + Guid.NewGuid().ToString("N"));

        Assert.Throws<UnauthorizedAccessException>(() => sut.CreateDirectory(outside));
    }

    [Fact]
    public void CreateDirectory_InsideAllowedRoot_CreatesDirectory() {
        using var temp = new TempDir();
        var sut = CreateSut(temp.Path);
        var target = Path.Combine(temp.Path, "Workflows", "Nested");

        sut.CreateDirectory(target);

        Assert.True(Directory.Exists(target));
    }

    [Fact]
    public void DeleteFile_InsideAllowedRoot_DeletesFile() {
        using var temp = new TempDir();
        var sut = CreateSut(temp.Path);
        var target = Path.Combine(temp.Path, "notes.md");
        File.WriteAllText(target, "x");

        sut.DeleteFile(target);

        Assert.False(File.Exists(target));
        Assert.False(sut.FileExists(target));
    }

    [Fact]
    public void DeleteFile_OutsideAllowedRoot_Throws() {
        using var temp = new TempDir();
        var sut = CreateSut(temp.Path);
        var outside = Path.Combine(Path.GetTempPath(), "outside-" + Guid.NewGuid().ToString("N"), "notes.md");
        Directory.CreateDirectory(Path.GetDirectoryName(outside)!);
        File.WriteAllText(outside, "x");

        try {
            Assert.Throws<UnauthorizedAccessException>(() => sut.DeleteFile(outside));
            Assert.True(File.Exists(outside));
        } finally {
            try { File.Delete(outside); Directory.Delete(Path.GetDirectoryName(outside)!); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void GetFileSize_ReturnsByteLength() {
        using var temp = new TempDir();
        var target = Path.Combine(temp.Path, "Main.xaml");
        File.WriteAllText(target, "<x/>");
        var sut = CreateSut(temp.Path);

        Assert.Equal(new FileInfo(target).Length, sut.GetFileSize(target));
    }

    [Fact]
    public void ReadAllText_InsideAllowedRoot_ReadsFile() {
        using var temp = new TempDir();
        var target = Path.Combine(temp.Path, "Main.xaml");
        File.WriteAllText(target, "<x/>");
        var sut = CreateSut(temp.Path);

        Assert.Equal("<x/>", sut.ReadAllText(target));
    }

    [Fact]
    public void ReadAllText_OutsideAllowedRoot_Throws() {
        using var temp = new TempDir();
        var sut = CreateSut(temp.Path);
        var outside = Path.Combine(Path.GetTempPath(), "outside-" + Guid.NewGuid().ToString("N"), "Main.xaml");
        Directory.CreateDirectory(Path.GetDirectoryName(outside)!);
        File.WriteAllText(outside, "<x/>");

        try {
            Assert.Throws<UnauthorizedAccessException>(() => sut.ReadAllText(outside));
            Assert.Throws<UnauthorizedAccessException>(() => sut.GetFileSize(outside));
            Assert.Throws<UnauthorizedAccessException>(() => sut.FileExists(outside));
        } finally {
            try { File.Delete(outside); Directory.Delete(Path.GetDirectoryName(outside)!); } catch { /* best effort */ }
        }
    }

    private sealed class TempDir : IDisposable {
        public string Path { get; } =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mcp-tests-" + Guid.NewGuid().ToString("N"));

        public TempDir() => Directory.CreateDirectory(Path);

        public void Dispose() {
            try { Directory.Delete(Path, recursive: true); } catch { /* best effort */ }
        }
    }
}
