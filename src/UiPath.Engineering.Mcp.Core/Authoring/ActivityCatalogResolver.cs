using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using UiPath.Engineering.Mcp.Core.Abstractions;
using UiPath.Engineering.Mcp.Core.Caching;
using UiPath.Engineering.Mcp.Core.Parsing;

namespace UiPath.Engineering.Mcp.Core.Authoring;

public sealed class ActivityCatalogResolver : IActivityCatalogResolver, IDisposable {
    public const int MaxRecommendations = 5;

    // A typical enterprise project references 15-30 activity packages. The old
    // budget of 8 per-project plus the wildcard query silently truncated the
    // catalog, so real activities were reported as unknown. The budget now covers
    // a large estate; whatever still exceeds it is reported, not swallowed.
    internal const int MaxPackageQueries = 32;
    private const int MaxDefaultXamlQueries = 24;

    private readonly IFilesystemProvider? _filesystem;
    private readonly IActivityDiscovery? _discovery;
    private readonly ILogger<ActivityCatalogResolver> _logger;
    private readonly BoundedCache<CachedCatalog> _cache = new();

    public ActivityCatalogResolver(
        IFilesystemProvider? filesystem = null,
        IActivityDiscovery? discovery = null,
        ILogger<ActivityCatalogResolver>? logger = null) {
        _filesystem = filesystem;
        _discovery = discovery;
        _logger = logger ?? NullLogger<ActivityCatalogResolver>.Instance;
    }

    public async Task<IActivityCatalog> ResolveAsync(string? projectPath, CancellationToken cancellationToken = default) {
        if (string.IsNullOrWhiteSpace(projectPath) || _filesystem is null) {
            return ActivityCatalog.Fallback;
        }

        var projectJson = _filesystem.FindProjectJson(projectPath);
        if (projectJson is null) {
            return ActivityCatalog.Fallback;
        }

        DateTime writeTime;
        try {
            writeTime = _filesystem.GetLastWriteTimeUtc(projectJson);
        } catch {
            writeTime = DateTime.MinValue;
        }

        var cacheKey = Path.GetFullPath(projectPath);
        return await _cache.RunExclusiveAsync(cacheKey, async ct => {
            if (_cache.TryGet(cacheKey, out var cached) && cached.ProjectJsonWriteTimeUtc == writeTime) {
                _logger.LogDebug("Activity catalog cache hit for {CacheKey}", cacheKey);
                return cached.Catalog;
            }

            _logger.LogDebug("Activity catalog cache miss for {CacheKey}", cacheKey);
            var packages = ReadPackages(projectJson);
            var (discovered, discoveryFailed, truncated) = await DiscoverForProjectAsync(projectPath, packages, ct);
            var catalog = Merge(
                ActivityCatalog.All,
                packages,
                discovered,
                discovered.Count > 0 ? "cli" : "project-packages",
                discoveryFailed,
                truncated);
            _cache.Set(cacheKey, new CachedCatalog(catalog, writeTime));
            return catalog;
        }, cancellationToken);
    }

    public void Dispose() => _cache.Dispose();

    public static string? DiscoveryWarning(IActivityCatalog catalog) {
        if (catalog.DiscoveryFailed) {
            return "Activity catalog discovery failed; unknown-activity checks used the fallback/project-packages catalog. Next: retry after the UiPath CLI is available, or pass unknownActivityEscapeHatch if the activity is real.";
        }

        if (catalog.TruncatedPackages is { Count: > 0 } truncated) {
            return $"Activity catalog discovery covered the first {MaxPackageQueries} activity packages; "
                + $"{truncated.Count} package(s) were not queried, so an activity from them may be reported as unknown: "
                + $"{string.Join(", ", truncated)}. Next: pass unknownActivityEscapeHatch, or install the package into a project and re-run.";
        }

        return null;
    }

    public async Task<IReadOnlyList<ActivityRecommendation>> RecommendAsync(
        string query, string? projectPath, int limit = MaxRecommendations, CancellationToken cancellationToken = default) {
        limit = Math.Clamp(limit, 1, MaxRecommendations);
        var catalog = await ResolveAsync(projectPath, cancellationToken);
        IReadOnlyList<DiscoveredActivity> queryHits = [];
        if (_discovery is not null && !string.IsNullOrWhiteSpace(projectPath)) {
            queryHits = await SafeFindAsync(projectPath, query, cancellationToken) ?? [];
        }

        var packages = ReadProjectPackages(projectPath);
        var merged = Merge(catalog.All, packages, queryHits, catalog.Source, catalog.DiscoveryFailed);
        var ranked = Rank(query, merged.All, packages);
        return ranked.Take(limit).ToList();
    }

    internal static IReadOnlyList<ActivityRecommendation> Rank(
        string query, IReadOnlyList<ActivitySchema> schemas, IReadOnlyDictionary<string, string> projectPackages) {
        if (string.IsNullOrWhiteSpace(query)) {
            return [];
        }

        var tokens = query.Split(new[] { ' ', '-', '_', '.', ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return schemas
            .Select(schema => ToRecommendation(schema, Score(query, tokens, schema), projectPackages, "catalog"))
            .Where(r => r.Score > 0)
            .OrderByDescending(r => r.Score)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    internal static ActivityRecommendation ToRecommendation(
        ActivitySchema schema, int score, IReadOnlyDictionary<string, string> projectPackages, string source) {
        string? needsPackage = null;
        if (!string.IsNullOrWhiteSpace(schema.PackageId)
            && !projectPackages.ContainsKey(schema.PackageId)
            && projectPackages.Count > 0) {
            needsPackage = string.IsNullOrWhiteSpace(schema.PackageVersion)
                ? schema.PackageId
                : $"{schema.PackageId}@{schema.PackageVersion}";
        }

        return new ActivityRecommendation {
            Name = schema.Name,
            FullTypeName = schema.FullTypeName,
            Prefix = schema.Prefix,
            XmlNamespace = schema.XmlNamespace,
            IsContainer = schema.IsContainer,
            PackageId = schema.PackageId,
            PackageVersion = schema.PackageVersion,
            Properties = schema.Properties,
            RequiredProperties = schema.Properties.Where(p => p.Required).Select(p => p.Name).ToList(),
            Score = score,
            NeedsPackage = needsPackage,
            Source = source
        };
    }

    internal static int Score(string query, IReadOnlyList<string> tokens, ActivitySchema schema) {
        var q = query.Trim();
        var name = schema.Name;
        var haystack = $"{schema.Name} {schema.FullTypeName} {schema.PackageId}";
        if (name.Equals(q, StringComparison.OrdinalIgnoreCase)) {
            return 100;
        }

        if (name.StartsWith(q, StringComparison.OrdinalIgnoreCase)) {
            return 80;
        }

        if (name.Contains(q, StringComparison.OrdinalIgnoreCase)) {
            return 60;
        }

        if (schema.FullTypeName is not null && schema.FullTypeName.Contains(q, StringComparison.OrdinalIgnoreCase)) {
            return 50;
        }

        if (schema.PackageId is not null && schema.PackageId.Contains(q, StringComparison.OrdinalIgnoreCase)) {
            return 40;
        }

        var tokenHits = tokens.Count(t => haystack.Contains(t, StringComparison.OrdinalIgnoreCase));
        if (tokenHits > 0 && tokens.Count > 0) {
            return 20 + (20 * tokenHits / tokens.Count);
        }

        return 0;
    }

    internal static IActivityCatalog Merge(
        IReadOnlyList<ActivitySchema> fallback,
        IReadOnlyDictionary<string, string> projectPackages,
        IReadOnlyList<DiscoveredActivity> discovered,
        string source,
        bool discoveryFailed = false,
        IReadOnlyList<string>? truncatedPackages = null) {
        var byName = new Dictionary<string, ActivitySchema>(StringComparer.OrdinalIgnoreCase);
        foreach (var schema in fallback) {
            byName[schema.Name] = StampVersion(schema, projectPackages);
        }

        foreach (var hit in discovered) {
            var converted = ToSchema(hit, projectPackages);
            if (byName.TryGetValue(converted.Name, out var existing)) {
                byName[converted.Name] = MergeSchema(existing, converted);
            } else {
                byName[converted.Name] = converted;
            }
        }

        return new ListActivityCatalog(byName.Values.ToList(), source, discoveryFailed) {
            TruncatedPackages = truncatedPackages ?? []
        };
    }

    // A discovery hit only adds facts the curated schema lacks: the package stamp,
    // the CLR type name, and (for a package-backed activity the fallback catalog
    // does not know at all) the starter's property surface. A curated schema keeps
    // its own properties — those are version-anchored and card-sourced.
    private static ActivitySchema MergeSchema(ActivitySchema curated, ActivitySchema discovered) {
        var properties = curated.Properties.Count > 0 ? curated.Properties : discovered.Properties;
        return curated with {
            PackageId = curated.PackageId ?? discovered.PackageId,
            PackageVersion = discovered.PackageVersion ?? curated.PackageVersion,
            FullTypeName = curated.FullTypeName ?? discovered.FullTypeName,
            Properties = properties,
            Body = curated.Body ?? discovered.Body
        };
    }

    internal static ActivitySchema ToSchema(DiscoveredActivity hit, IReadOnlyDictionary<string, string> projectPackages) {
        var (prefix, ns) = ActivityFindParser.InferNamespace(hit.XmlNamespace, hit.FullTypeName ?? hit.Name, hit.Name);
        var properties = hit.Properties is { Count: > 0 }
            ? hit.Properties
            : [new PropertySchema("DisplayName", false, PropertyKind.Literal)];
        var schema = new ActivitySchema(
            hit.Name,
            hit.Prefix ?? prefix,
            hit.XmlNamespace ?? ns,
            hit.IsContainer,
            properties,
            PackageId: hit.PackageId,
            PackageVersion: ActivityFindParser.StripVersion(hit.PackageVersion),
            FullTypeName: hit.FullTypeName,
            Body: hit.Body);
        return StampVersion(schema, projectPackages);
    }

    private static ActivitySchema StampVersion(ActivitySchema schema, IReadOnlyDictionary<string, string> projectPackages) {
        if (schema.PackageId is not null && projectPackages.TryGetValue(schema.PackageId, out var version)) {
            return schema with { PackageVersion = version };
        }

        return schema;
    }

    private IReadOnlyDictionary<string, string> ReadProjectPackages(string? projectPath) {
        if (string.IsNullOrWhiteSpace(projectPath) || _filesystem is null) {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        var projectJson = _filesystem.FindProjectJson(projectPath);
        return projectJson is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : ReadPackages(projectJson);
    }

    private IReadOnlyDictionary<string, string> ReadPackages(string projectJsonPath) {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (_filesystem is null) {
            return map;
        }

        try {
            var model = new ProjectJsonParser(_filesystem).Parse(projectJsonPath, Path.GetDirectoryName(projectJsonPath) ?? "");
            foreach (var package in model.Packages) {
                map[package.Id] = ActivityFindParser.StripVersion(package.Version) ?? package.Version;
            }
        } catch {
            // Malformed project.json: keep the fallback catalog without versions.
        }

        return map;
    }

    private async Task<(IReadOnlyList<DiscoveredActivity> Hits, bool Failed, IReadOnlyList<string> Truncated)> DiscoverForProjectAsync(
        string projectPath, IReadOnlyDictionary<string, string> packages, CancellationToken cancellationToken) {
        if (_discovery is null) {
            return ([], false, []);
        }

        var hits = new List<DiscoveredActivity>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var failed = false;

        foreach (var query in DiscoveryQueries(packages.Keys)) {
            var found = await SafeFindAsync(projectPath, query, cancellationToken);
            if (found is null) {
                failed = true;
                continue;
            }

            foreach (var hit in found) {
                if (seen.Add($"{hit.PackageId}|{hit.Name}|{hit.FullTypeName}")) {
                    hits.Add(hit);
                }
            }
        }

        if (!failed) {
            await EnrichWithDefaultXamlAsync(projectPath, hits, cancellationToken);
        }

        return (hits, failed, TruncatedPackages(packages.Keys));
    }

    internal static IEnumerable<string> DiscoveryQueries(IEnumerable<string> packageIds) {
        yield return "*";
        foreach (var id in ActivityPackages(packageIds).Take(MaxPackageQueries)) {
            yield return id;
        }
    }

    internal static IEnumerable<string> ActivityPackages(IEnumerable<string> packageIds) =>
        packageIds.Where(id => id.Contains("Activities", StringComparison.OrdinalIgnoreCase)
                               || id.StartsWith("UiPath.", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase);

    internal static IReadOnlyList<string> TruncatedPackages(IEnumerable<string> packageIds) =>
        ActivityPackages(packageIds).Skip(MaxPackageQueries).ToList();

    // A discovered activity whose property surface is not on the hand-written card
    // gets the package's own starter, so validate_activity_spec checks the real
    // property names instead of validating any property bag as valid.
    private async Task EnrichWithDefaultXamlAsync(
        string projectPath, List<DiscoveredActivity> hits, CancellationToken cancellationToken) {
        if (_discovery is null) {
            return;
        }

        var budget = MaxDefaultXamlQueries;
        for (var i = 0; i < hits.Count; i++) {
            if (budget <= 0) {
                _logger.LogDebug("Default-XAML budget exhausted; {Remaining} discovered activities keep their discover-time schema.", hits.Count - i);
                break;
            }

            var hit = hits[i];
            if (ActivityCatalog.CommonActivityCard.Contains(hit.Name)
                || string.IsNullOrWhiteSpace(hit.FullTypeName)
                || hit.Properties is { Count: > 0 }) {
                continue;
            }

            budget--;
            var surface = await SafeDefaultXamlAsync(projectPath, hit.FullTypeName!, cancellationToken);
            if (surface is not null) {
                hits[i] = hit with {
                    Properties = surface.Properties,
                    IsContainer = surface.IsContainer,
                    XmlNamespace = string.IsNullOrWhiteSpace(hit.XmlNamespace) ? surface.XmlNamespace : hit.XmlNamespace,
                    Prefix = string.IsNullOrWhiteSpace(hit.Prefix)
                        ? ActivityFindParser.InferNamespace(surface.XmlNamespace, hit.FullTypeName!, hit.Name).Prefix
                        : hit.Prefix
                };
            }
        }
    }

    private async Task<DefaultXamlSurface?> SafeDefaultXamlAsync(
        string projectPath, string activityClassName, CancellationToken cancellationToken) {
        if (_discovery is null) {
            return null;
        }

        try {
            var xaml = await _discovery.GetDefaultXamlAsync(projectPath, activityClassName, cancellationToken);
            return DefaultXamlParser.Parse(xaml);
        } catch (Exception ex) {
            _logger.LogDebug(ex, "Default-XAML lookup failed for {ActivityClassName}", activityClassName);
            return null;
        }
    }

    private async Task<IReadOnlyList<DiscoveredActivity>?> SafeFindAsync(
        string projectPath, string query, CancellationToken cancellationToken) {
        if (_discovery is null) {
            return [];
        }

        try {
            return await _discovery.FindAsync(projectPath, query, cancellationToken) ?? [];
        } catch (Exception ex) {
            _logger.LogWarning(ex, "Activity catalog discovery failed for query {Query}", query);
            return null;
        }
    }

    private sealed record CachedCatalog(IActivityCatalog Catalog, DateTime ProjectJsonWriteTimeUtc);
}
