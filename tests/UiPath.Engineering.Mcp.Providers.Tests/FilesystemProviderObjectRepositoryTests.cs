using UiPath.Engineering.Mcp.Core;
using UiPath.Engineering.Mcp.Providers.Filesystem;

namespace UiPath.Engineering.Mcp.Providers.Tests;

/// <summary>
/// The Object Repository read path: `.objects` is visible to the folder tree and readable by
/// exact path, but is never a workflow/source discovery target — UiPath stores OR metadata there
/// that the project model must not treat as `.xaml` workflows. `.local` stays hidden everywhere.
/// </summary>
public class FilesystemProviderObjectRepositoryTests {
    private static FilesystemProvider CreateSut(params string[] roots) =>
        new(new PathPolicy(roots));

    private static string Seed(string root, string relativePath, string content) {
        var full = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    [Fact]
    public void GetDirectoryTree_ExposesTheObjectRepositoryFolder() {
        using var temp = new TempDir();
        Seed(temp.Path, "Main.xaml", "<x/>");
        Seed(temp.Path, ".objects/.metadata", "{}");
        Seed(temp.Path, ".objects/ocam/.metadata", "{}");

        var tree = CreateSut(temp.Path).GetDirectoryTree(temp.Path);

        var names = tree.Children.Select(c => c.Name).ToList();
        Assert.Contains("Main.xaml", names);
        Assert.Contains(".objects", names);
    }

    [Fact]
    public void GetDirectoryTree_StillHidesDotLocal() {
        using var temp = new TempDir();
        Seed(temp.Path, ".local/docs/packages/P/overview.md", "# overview");

        var tree = CreateSut(temp.Path).GetDirectoryTree(temp.Path);

        Assert.DoesNotContain(".local", tree.Children.Select(c => c.Name));
    }

    [Fact]
    public void GetDirectoryTree_DoesNotDescendIntoObjectRepositoryPastMaxDepth() {
        // A real .objects tree runs thousands of files deep; the depth cap keeps it bounded.
        using var temp = new TempDir();
        Seed(temp.Path, ".objects/ocam/zMr_/SE4V/-QKm/.metadata", "{}");

        var tree = CreateSut(temp.Path).GetDirectoryTree(temp.Path, maxDepth: 2);

        var objects = tree.Children.Single(c => c.Name == ".objects");
        var ocam = Assert.Single(objects.Children, c => c.Name == "ocam");
        Assert.Empty(ocam.Children); // depth limit reached: zMr_ and deeper are not enumerated
    }

    [Fact]
    public void FindXamlFiles_NeverReturnsObjectRepositoryContent() {
        using var temp = new TempDir();
        Seed(temp.Path, "Main.xaml", "<x/>");
        // An .xaml-shaped file inside .objects must not become a workflow.
        Seed(temp.Path, ".objects/Fake.xaml", "<x/>");
        Seed(temp.Path, ".objects/ocam/.metadata", "{}");

        var files = CreateSut(temp.Path).FindXamlFiles(temp.Path).Select(Path.GetFileName).ToList();

        Assert.Equal(["Main.xaml"], files);
    }

    [Fact]
    public void FindCSharpFiles_NeverReturnsObjectRepositoryContent() {
        using var temp = new TempDir();
        Seed(temp.Path, "InvoiceFlow.cs", "// code");
        Seed(temp.Path, ".objects/Generated.cs", "// code");

        var files = CreateSut(temp.Path).FindCSharpFiles(temp.Path).Select(Path.GetFileName).ToList();

        Assert.Equal(["InvoiceFlow.cs"], files);
    }

    [Fact]
    public void FindXamlFiles_StillSkipsDotLocal() {
        using var temp = new TempDir();
        Seed(temp.Path, "Main.xaml", "<x/>");
        Seed(temp.Path, ".local/cached.xaml", "<x/>");

        var files = CreateSut(temp.Path).FindXamlFiles(temp.Path).Select(Path.GetFileName).ToList();

        Assert.Equal(["Main.xaml"], files);
    }

    [Fact]
    public void ReadAllText_ReadsObjectRepositoryMetadataByExactPath() {
        using var temp = new TempDir();
        var metadata = Seed(temp.Path, ".objects/.metadata", """{"Type":"Library"}""");
        var sut = CreateSut(temp.Path);

        // Read the enumerated path directly; EnsureAllowed passes for anything inside the roots.
        Assert.Equal("""{"Type":"Library"}""", sut.ReadAllText(metadata));
    }

    [Fact]
    public void ReadAllText_OutsideAllowedRoots_StillThrows_ForObjectRepositoryPaths() {
        using var temp = new TempDir();
        var outside = Path.Combine(Path.GetTempPath(), "mcp-outside-" + Guid.NewGuid().ToString("N"));
        var metadata = Seed(outside, ".objects/.metadata", "{}");
        var sut = CreateSut(temp.Path);

        try {
            Assert.Throws<UnauthorizedAccessException>(() => sut.ReadAllText(metadata));
        } finally {
            try { Directory.Delete(outside, recursive: true); } catch { /* best effort */ }
        }
    }

    private sealed class TempDir : IDisposable {
        public string Path { get; } =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mcp-or-tests-" + Guid.NewGuid().ToString("N"));

        public TempDir() => Directory.CreateDirectory(Path);

        public void Dispose() {
            try { Directory.Delete(Path, recursive: true); } catch { /* best effort */ }
        }
    }
}
