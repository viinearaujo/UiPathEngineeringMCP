using UiPath.Engineering.Mcp.Core.Caching;

namespace UiPath.Engineering.Mcp.Core.Tests;

public class ProjectFileChangeWatcherTests {
    [Fact]
    public void ForProvider_FakeFilesystem_ReturnsNullFactory() {
        var fs = new FakeFilesystemProvider();
        Assert.Null(ProjectFileChangeWatcher.ForProvider(fs));
    }

    [Fact]
    public void TryStart_MissingDirectory_IsInactive() {
        var watcher = ProjectFileChangeWatcher.TryStart(
            Path.Combine(Path.GetTempPath(), "mcp-watch-missing-" + Guid.NewGuid().ToString("N")));
        Assert.False(watcher.IsActive);
        Assert.True(watcher.IsDirty);
        watcher.Dispose();
    }

    [Fact]
    public void TryStart_RealDirectory_StartsActiveAndTracksDirty() {
        var root = Path.Combine(Path.GetTempPath(), "mcp-watch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try {
            using var watcher = ProjectFileChangeWatcher.TryStart(root);
            Assert.True(watcher.IsActive);
            Assert.True(watcher.IsDirty);
            watcher.MarkClean();
            Assert.False(watcher.IsDirty);

            File.WriteAllText(Path.Combine(root, "Main.xaml"), "<Activity/>");
            // FileSystemWatcher is asynchronous; poll briefly for the dirty flag.
            var deadline = DateTime.UtcNow.AddSeconds(2);
            while (!watcher.IsDirty && DateTime.UtcNow < deadline) {
                Thread.Sleep(50);
            }

            Assert.True(watcher.IsDirty);
        } finally {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task FingerprintedCache_DisposesWatcherOnEviction() {
        var disposed = new List<string>();
        var factory = new TrackingFactory(disposed);
        using var cache = new FingerprintedCache<string>(
            "test",
            maxEntries: 1,
            watcherFactory: factory,
            reuseWhenClean: true);

        Assert.Equal("a", await cache.GetOrBuildAsync("/p/a", _ => "fa", _ => Task.FromResult("a"), (_, _) => { }));
        Assert.Equal("b", await cache.GetOrBuildAsync("/p/b", _ => "fb", _ => Task.FromResult("b"), (_, _) => { }));

        Assert.Contains(disposed, d => d.Contains("a", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class TrackingFactory(List<string> disposed) : IProjectChangeWatcherFactory {
        public IProjectChangeWatcher? TryCreate(string projectPath) =>
            new TrackingWatcher(projectPath, disposed);
    }

    private sealed class TrackingWatcher(string path, List<string> disposed) : IProjectChangeWatcher {
        public bool IsActive => true;
        public bool IsDirty { get; private set; } = true;
        public void MarkClean() => IsDirty = false;
        public void Dispose() => disposed.Add(path);
    }
}
