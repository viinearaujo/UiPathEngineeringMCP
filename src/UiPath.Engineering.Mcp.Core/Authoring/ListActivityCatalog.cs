using System.Diagnostics.CodeAnalysis;

namespace UiPath.Engineering.Mcp.Core.Authoring;

/// <summary>
/// An immutable catalog over a fixed schema list. The name index resolves every
/// schema by its spec name and by the element name Studio emits, via
/// <see cref="ActivityCatalog.BuildIdentityLookup"/>.
/// </summary>
/// <remarks>
/// Indexing by spec name alone is not enough, and the failure is silent: the spec
/// name is the toolbox label ("While") while the emitted element is the type
/// Studio serializes ("InterruptibleWhile"). A caller holding a catalog —
/// <c>get_activity_metadata</c>, <c>validate_activity_spec</c> — may see either.
///
/// The short UIA alias ("Click" for "NClick") is deliberately NOT indexed here.
/// <see cref="XamlCatalogGuard"/> resolves XAML element names through this type,
/// and those names are namespace-blind — <c>ui:Click</c> (legacy), <c>uix:NClick</c>
/// and the mobile <c>Click</c> are indistinguishable by local name. Indexing the
/// bare "Click" would make the guard accept the legacy and mobile forms as
/// instances of the modern UIA activity. The alias is a launcher convenience for
/// callers that pass their own namespace, so it lives on
/// <see cref="ActivityCatalog.TryGet"/> only.
/// </remarks>
public sealed class ListActivityCatalog : IActivityCatalog {
    private readonly IReadOnlyDictionary<string, ActivitySchema> _byName;

    public ListActivityCatalog(IReadOnlyList<ActivitySchema> all, string source, bool discoveryFailed = false) {
        All = all;
        Source = source;
        DiscoveryFailed = discoveryFailed;
        _byName = ActivityCatalog.BuildIdentityLookup(all);
    }

    public IReadOnlyList<ActivitySchema> All { get; }
    public string Source { get; }
    public bool DiscoveryFailed { get; }

    /// <summary>
    /// Packages whose activities were dropped from the discovery query fan-out
    /// because the per-project query budget ran out. Empty when nothing was
    /// truncated — the caller surfaces the rest as a warning.
    /// </summary>
    public IReadOnlyList<string> TruncatedPackages { get; init; } = [];

    public bool TryGet(string name, [NotNullWhen(true)] out ActivitySchema? schema) =>
        _byName.TryGetValue(name, out schema);

    public string? Suggest(string name) => ActivityCatalog.Suggest(name, All);
}
