namespace UiPath.Engineering.Mcp.Core.CodeAnalysis;

/// <summary>
/// Result of resolving a UiPath project's compilation references: assembly file
/// paths plus bookkeeping about what could not be resolved. Pure path selection —
/// no assembly is loaded at this stage.
/// </summary>
public sealed class ReferenceResolution {
    public List<string> AssemblyPaths { get; set; } = [];
    public List<string> UnresolvedDependencies { get; set; } = [];
    public bool FrameworkResolved { get; set; }
    public bool PackagesFolderFound { get; set; }
}
