using System.Diagnostics.CodeAnalysis;

namespace UiPath.Engineering.Mcp.Core.Authoring;

public interface IActivityCatalog {
    IReadOnlyList<ActivitySchema> All { get; }
    string Source { get; }
    bool DiscoveryFailed { get; }

    /// <summary>
    /// Packages whose activities were not queried because the per-project query
    /// budget ran out. Empty when discovery covered every package; a non-empty
    /// list means the catalog is incomplete and the caller should say so rather
    /// than treat an unknown activity as non-existent.
    /// </summary>
    IReadOnlyList<string> TruncatedPackages => [];

    bool TryGet(string name, [NotNullWhen(true)] out ActivitySchema? schema);
    string? Suggest(string name);
}
