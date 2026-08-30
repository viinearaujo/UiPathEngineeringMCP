namespace UiPath.Engineering.Mcp.Core.Authoring;

/// <summary>
/// Project-scoped activity metadata (typically <c>uip rpa activities find</c>).
/// Implementations must not throw for a missing CLI — return an empty list instead.
/// </summary>
public interface IActivityDiscovery
{
    Task<IReadOnlyList<DiscoveredActivity>> FindAsync(
        string projectPath, string query, CancellationToken cancellationToken = default);
}

public sealed record DiscoveredActivity(
    string Name,
    string? FullTypeName = null,
    string? PackageId = null,
    string? PackageVersion = null,
    string? XmlNamespace = null,
    string? Prefix = null,
    bool IsContainer = true,
    IReadOnlyList<PropertySchema>? Properties = null);
