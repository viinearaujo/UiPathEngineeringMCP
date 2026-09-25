namespace UiPath.Engineering.Mcp.Core.Caching;

/// <summary>
/// Per-project change signal for fingerprint reuse. When <see cref="IsActive"/> and
/// not <see cref="IsDirty"/>, the last fingerprint may be reused without walking the tree.
/// </summary>
public interface IProjectChangeWatcher : IDisposable {
    /// <summary>True when the watcher started successfully and can be trusted.</summary>
    bool IsActive { get; }

    /// <summary>True when a watched file may have changed since <see cref="MarkClean"/>.</summary>
    bool IsDirty { get; }

    /// <summary>Clears the dirty flag after a successful fingerprint walk.</summary>
    void MarkClean();
}

/// <summary>Creates per-project change watchers, or returns null to force walk-every-time.</summary>
public interface IProjectChangeWatcherFactory {
    IProjectChangeWatcher? TryCreate(string projectPath);
}
