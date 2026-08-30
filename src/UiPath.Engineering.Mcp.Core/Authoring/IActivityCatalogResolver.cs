namespace UiPath.Engineering.Mcp.Core.Authoring;

public interface IActivityCatalogResolver
{
    Task<IActivityCatalog> ResolveAsync(string? projectPath, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ActivityRecommendation>> RecommendAsync(
        string query, string? projectPath, int limit = 5, CancellationToken cancellationToken = default);
}

public sealed class ActivityRecommendation
{
    public string Name { get; init; } = string.Empty;
    public string? FullTypeName { get; init; }
    public string Prefix { get; init; } = string.Empty;
    public string XmlNamespace { get; init; } = string.Empty;
    public bool IsContainer { get; init; }
    public string? PackageId { get; init; }
    public string? PackageVersion { get; init; }
    public IReadOnlyList<PropertySchema> Properties { get; init; } = [];
    public IReadOnlyList<string> RequiredProperties { get; init; } = [];
    public int Score { get; init; }
    public string? NeedsPackage { get; init; }
    public string Source { get; init; } = string.Empty;
}
