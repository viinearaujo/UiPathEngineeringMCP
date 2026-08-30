using System.Collections.Concurrent;
using UiPath.Engineering.Mcp.Core.Abstractions;
using UiPath.Engineering.Mcp.Core.Parsing;

namespace UiPath.Engineering.Mcp.Core.Authoring;

public sealed class ActivityCatalogResolver : IActivityCatalogResolver
{
    public const int MaxRecommendations = 5;
    private const int MaxPackageQueries = 8;

    private readonly IFilesystemProvider? _filesystem;
    private readonly IActivityDiscovery? _discovery;
    private readonly ConcurrentDictionary<string, CachedCatalog> _cache = new(StringComparer.OrdinalIgnoreCase);

    public ActivityCatalogResolver(IFilesystemProvider? filesystem = null, IActivityDiscovery? discovery = null)
    {
        _filesystem = filesystem;
        _discovery = discovery;
    }

    public async Task<IActivityCatalog> ResolveAsync(string? projectPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(projectPath) || _filesystem is null)
        {
            return ActivityCatalog.Fallback;
        }

        var projectJson = _filesystem.FindProjectJson(projectPath);
        if (projectJson is null)
        {
            return ActivityCatalog.Fallback;
        }

        DateTime writeTime;
        try
        {
            writeTime = _filesystem.GetLastWriteTimeUtc(projectJson);
        }
        catch
        {
            writeTime = DateTime.MinValue;
        }

        var cacheKey = Path.GetFullPath(projectPath);
        if (_cache.TryGetValue(cacheKey, out var cached) && cached.ProjectJsonWriteTimeUtc == writeTime)
        {
            return cached.Catalog;
        }

        var packages = ReadPackages(projectJson);
        var discovered = await DiscoverForProjectAsync(projectPath, packages, cancellationToken);
        var catalog = Merge(ActivityCatalog.All, packages, discovered, discovered.Count > 0 ? "cli" : "project-packages");
        _cache[cacheKey] = new CachedCatalog(catalog, writeTime);
        return catalog;
    }

    public async Task<IReadOnlyList<ActivityRecommendation>> RecommendAsync(
        string query, string? projectPath, int limit = MaxRecommendations, CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 1, MaxRecommendations);
        var catalog = await ResolveAsync(projectPath, cancellationToken);
        IReadOnlyList<DiscoveredActivity> queryHits = [];
        if (_discovery is not null && !string.IsNullOrWhiteSpace(projectPath))
        {
            queryHits = await SafeFindAsync(projectPath, query, cancellationToken);
        }

        var packages = ReadProjectPackages(projectPath);
        var merged = Merge(catalog.All, packages, queryHits, catalog.Source);
        var ranked = Rank(query, merged.All, packages);
        return ranked.Take(limit).ToList();
    }

    internal static IReadOnlyList<ActivityRecommendation> Rank(
        string query, IReadOnlyList<ActivitySchema> schemas, IReadOnlyDictionary<string, string> projectPackages)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
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
        ActivitySchema schema, int score, IReadOnlyDictionary<string, string> projectPackages, string source)
    {
        string? needsPackage = null;
        if (!string.IsNullOrWhiteSpace(schema.PackageId)
            && !projectPackages.ContainsKey(schema.PackageId)
            && projectPackages.Count > 0)
        {
            needsPackage = string.IsNullOrWhiteSpace(schema.PackageVersion)
                ? schema.PackageId
                : $"{schema.PackageId}@{schema.PackageVersion}";
        }

        return new ActivityRecommendation
        {
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

    internal static int Score(string query, IReadOnlyList<string> tokens, ActivitySchema schema)
    {
        var q = query.Trim();
        var name = schema.Name;
        var haystack = $"{schema.Name} {schema.FullTypeName} {schema.PackageId}";
        if (name.Equals(q, StringComparison.OrdinalIgnoreCase))
        {
            return 100;
        }

        if (name.StartsWith(q, StringComparison.OrdinalIgnoreCase))
        {
            return 80;
        }

        if (name.Contains(q, StringComparison.OrdinalIgnoreCase))
        {
            return 60;
        }

        if (schema.FullTypeName is not null && schema.FullTypeName.Contains(q, StringComparison.OrdinalIgnoreCase))
        {
            return 50;
        }

        if (schema.PackageId is not null && schema.PackageId.Contains(q, StringComparison.OrdinalIgnoreCase))
        {
            return 40;
        }

        var tokenHits = tokens.Count(t => haystack.Contains(t, StringComparison.OrdinalIgnoreCase));
        if (tokenHits > 0 && tokens.Count > 0)
        {
            return 20 + (20 * tokenHits / tokens.Count);
        }

        return 0;
    }

    internal static IActivityCatalog Merge(
        IReadOnlyList<ActivitySchema> fallback,
        IReadOnlyDictionary<string, string> projectPackages,
        IReadOnlyList<DiscoveredActivity> discovered,
        string source)
    {
        var byName = new Dictionary<string, ActivitySchema>(StringComparer.OrdinalIgnoreCase);
        foreach (var schema in fallback)
        {
            byName[schema.Name] = StampVersion(schema, projectPackages);
        }

        foreach (var hit in discovered)
        {
            var converted = ToSchema(hit, projectPackages);
            if (byName.TryGetValue(converted.Name, out var existing))
            {
                byName[converted.Name] = existing with
                {
                    PackageId = existing.PackageId ?? converted.PackageId,
                    PackageVersion = converted.PackageVersion ?? existing.PackageVersion,
                    FullTypeName = existing.FullTypeName ?? converted.FullTypeName
                };
            }
            else
            {
                byName[converted.Name] = converted;
            }
        }

        return new ListActivityCatalog(byName.Values.ToList(), source);
    }

    internal static ActivitySchema ToSchema(DiscoveredActivity hit, IReadOnlyDictionary<string, string> projectPackages)
    {
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
            FullTypeName: hit.FullTypeName);
        return StampVersion(schema, projectPackages);
    }

    private static ActivitySchema StampVersion(ActivitySchema schema, IReadOnlyDictionary<string, string> projectPackages)
    {
        if (schema.PackageId is not null && projectPackages.TryGetValue(schema.PackageId, out var version))
        {
            return schema with { PackageVersion = version };
        }

        return schema;
    }

    private IReadOnlyDictionary<string, string> ReadProjectPackages(string? projectPath)
    {
        if (string.IsNullOrWhiteSpace(projectPath) || _filesystem is null)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        var projectJson = _filesystem.FindProjectJson(projectPath);
        return projectJson is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : ReadPackages(projectJson);
    }

    private IReadOnlyDictionary<string, string> ReadPackages(string projectJsonPath)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (_filesystem is null)
        {
            return map;
        }

        try
        {
            var model = new ProjectJsonParser(_filesystem).Parse(projectJsonPath, Path.GetDirectoryName(projectJsonPath) ?? "");
            foreach (var package in model.Packages)
            {
                map[package.Id] = ActivityFindParser.StripVersion(package.Version) ?? package.Version;
            }
        }
        catch
        {
            // Malformed project.json: keep the fallback catalog without versions.
        }

        return map;
    }

    private async Task<IReadOnlyList<DiscoveredActivity>> DiscoverForProjectAsync(
        string projectPath, IReadOnlyDictionary<string, string> packages, CancellationToken cancellationToken)
    {
        if (_discovery is null)
        {
            return [];
        }

        var hits = new List<DiscoveredActivity>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var query in DiscoveryQueries(packages.Keys))
        {
            foreach (var hit in await SafeFindAsync(projectPath, query, cancellationToken))
            {
                if (seen.Add($"{hit.PackageId}|{hit.Name}|{hit.FullTypeName}"))
                {
                    hits.Add(hit);
                }
            }
        }

        return hits;
    }

    internal static IEnumerable<string> DiscoveryQueries(IEnumerable<string> packageIds)
    {
        yield return "*";
        var activityPackages = packageIds
            .Where(id => id.Contains("Activities", StringComparison.OrdinalIgnoreCase)
                         || id.StartsWith("UiPath.", StringComparison.OrdinalIgnoreCase))
            .Take(MaxPackageQueries);
        foreach (var id in activityPackages)
        {
            yield return id;
        }
    }

    private async Task<IReadOnlyList<DiscoveredActivity>> SafeFindAsync(
        string projectPath, string query, CancellationToken cancellationToken)
    {
        if (_discovery is null)
        {
            return [];
        }

        try
        {
            return await _discovery.FindAsync(projectPath, query, cancellationToken) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private sealed record CachedCatalog(IActivityCatalog Catalog, DateTime ProjectJsonWriteTimeUtc);
}
