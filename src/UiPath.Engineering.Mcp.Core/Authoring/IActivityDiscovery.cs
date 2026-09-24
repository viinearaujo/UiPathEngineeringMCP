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

/// <summary>
/// One activity discovered from the project (typically <c>uip rpa activities find</c>).
/// <paramref name="PropertiesAreComplete"/> says whether
/// <paramref name="Properties"/> is the activity's complete settable surface.
/// Discovery surfaces start as samples (false): the CLI property array and the
/// package's default XAML both enumerate only some properties, so the validator
/// tolerates properties the sample does not name. Reflection over the activity
/// assemblies sets it true once it has read the whole surface.
/// </summary>
public sealed record DiscoveredActivity(
    string Name,
    string? FullTypeName = null,
    string? PackageId = null,
    string? PackageVersion = null,
    string? XmlNamespace = null,
    string? Prefix = null,
    bool IsContainer = true,
    IReadOnlyList<PropertySchema>? Properties = null,
    BodyDescriptor? Body = null,
    bool PropertiesAreComplete = false);
