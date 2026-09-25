using UiPath.Engineering.Mcp.Core.Abstractions;

namespace UiPath.Engineering.Mcp.Core.Caching;

/// <summary>
/// <see cref="FileSystemWatcher"/> over project.json / *.xaml / *.cs. Ignores bin, obj,
/// and .git. A failed start yields an inactive watcher so callers fall back to walking.
/// </summary>
public sealed class ProjectFileChangeWatcher : IProjectChangeWatcher {
    private static readonly string[] IgnoredDirectoryNames = [".git", "bin", "obj"];

    private readonly FileSystemWatcher? _watcher;
    private readonly object _gate = new();
    private bool _dirty = true;
    private bool _disposed;

    private ProjectFileChangeWatcher(FileSystemWatcher? watcher, bool active) {
        _watcher = watcher;
        IsActive = active;
    }

    public bool IsActive { get; }

    public bool IsDirty {
        get {
            lock (_gate) {
                return _dirty;
            }
        }
    }

    public static IProjectChangeWatcherFactory Factory { get; } = new FactoryImpl();

    /// <summary>
    /// Returns the OS watcher factory only for the real <c>FilesystemProvider</c>; fake
    /// in-memory providers get null so tests keep walking without a real watcher.
    /// </summary>
    public static IProjectChangeWatcherFactory? ForProvider(IFilesystemProvider filesystem) =>
        filesystem.GetType().Name == "FilesystemProvider" ? Factory : null;

    public void MarkClean() {
        lock (_gate) {
            _dirty = false;
        }
    }

    public void Dispose() {
        if (_disposed) {
            return;
        }

        _disposed = true;
        if (_watcher is null) {
            return;
        }

        _watcher.EnableRaisingEvents = false;
        _watcher.Changed -= OnChanged;
        _watcher.Created -= OnChanged;
        _watcher.Deleted -= OnChanged;
        _watcher.Renamed -= OnRenamed;
        _watcher.Error -= OnError;
        _watcher.Dispose();
    }

    internal static ProjectFileChangeWatcher TryStart(string projectPath) {
        string full;
        try {
            full = Path.GetFullPath(projectPath);
        } catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) {
            return new ProjectFileChangeWatcher(null, active: false);
        }

        if (!Directory.Exists(full)) {
            return new ProjectFileChangeWatcher(null, active: false);
        }

        try {
            var watcher = new FileSystemWatcher(full) {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName
                    | NotifyFilters.DirectoryName
                    | NotifyFilters.LastWrite
                    | NotifyFilters.CreationTime
                    | NotifyFilters.Size,
                Filter = "*.*"
            };
            var instance = new ProjectFileChangeWatcher(watcher, active: true);
            watcher.Changed += instance.OnChanged;
            watcher.Created += instance.OnChanged;
            watcher.Deleted += instance.OnChanged;
            watcher.Renamed += instance.OnRenamed;
            watcher.Error += instance.OnError;
            watcher.EnableRaisingEvents = true;
            return instance;
        } catch (Exception ex) when (ex is ArgumentException
            or UnauthorizedAccessException
            or PlatformNotSupportedException
            or IOException) {
            return new ProjectFileChangeWatcher(null, active: false);
        }
    }

    private void OnChanged(object sender, FileSystemEventArgs e) {
        if (IsWatched(e.FullPath, e.Name)) {
            SetDirty();
        }
    }

    private void OnRenamed(object sender, RenamedEventArgs e) {
        if (IsWatched(e.FullPath, e.Name) || IsWatched(e.OldFullPath, e.OldName)) {
            SetDirty();
        }
    }

    private void OnError(object sender, ErrorEventArgs e) {
        // Buffer overflow or watcher failure: force walks until disposed/recreated.
        SetDirty();
    }

    private void SetDirty() {
        lock (_gate) {
            _dirty = true;
        }
    }

    private static bool IsWatched(string? fullPath, string? name) {
        if (string.IsNullOrEmpty(fullPath) && string.IsNullOrEmpty(name)) {
            return false;
        }

        if (IsUnderIgnoredDirectory(fullPath)) {
            return false;
        }

        var fileName = !string.IsNullOrEmpty(name) ? Path.GetFileName(name) : Path.GetFileName(fullPath);
        if (string.IsNullOrEmpty(fileName)) {
            // Directory events under the project still invalidate the file list.
            return true;
        }

        if (fileName.Equals("project.json", StringComparison.OrdinalIgnoreCase)) {
            return true;
        }

        return fileName.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsUnderIgnoredDirectory(string? fullPath) {
        if (string.IsNullOrEmpty(fullPath)) {
            return false;
        }

        var parts = fullPath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts) {
            foreach (var ignored in IgnoredDirectoryNames) {
                if (part.Equals(ignored, StringComparison.OrdinalIgnoreCase)) {
                    return true;
                }
            }
        }

        return false;
    }

    private sealed class FactoryImpl : IProjectChangeWatcherFactory {
        public IProjectChangeWatcher? TryCreate(string projectPath) => TryStart(projectPath);
    }
}
