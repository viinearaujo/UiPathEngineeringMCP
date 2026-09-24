namespace UiPath.Engineering.Mcp.Core.Authoring;

/// <summary>
/// Project-scoped activity metadata (typically <c>uip rpa activities find</c>).
/// Implementations must not throw for a missing CLI — return an empty list instead.
/// </summary>
public interface IActivityDiscovery {
    Task<IReadOnlyList<DiscoveredActivity>> FindAsync(
        string projectPath, string query, CancellationToken cancellationToken = default);

    /// <summary>
    /// Starter XAML for one activity class (<c>uip rpa activities get-default-xaml</c>):
    /// the element with the namespaces, assembly references, and the properties whose
    /// values differ from the type default. Returns null when the CLI cannot produce a
    /// starter. The output carries only non-default properties — it is a surface
    /// sample, never a complete property list.
    /// </summary>
    Task<string?> GetDefaultXamlAsync(
        string projectPath, string activityClassName, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
}

public sealed record DiscoveredActivity(
    string Name,
    string? FullTypeName = null,
    string? PackageId = null,
    string? PackageVersion = null,
    string? XmlNamespace = null,
    string? Prefix = null,
    bool IsContainer = true,
    IReadOnlyList<PropertySchema>? Properties = null,
    BodyDescriptor? Body = null);
