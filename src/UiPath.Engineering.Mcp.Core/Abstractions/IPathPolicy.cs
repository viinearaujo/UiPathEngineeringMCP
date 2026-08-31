namespace UiPath.Engineering.Mcp.Core.Abstractions;

/// <summary>
/// Single path + secret policy for tools, resources, and the filesystem provider:
/// allowlist, project-relative resolve, blocked names, and max size.
/// </summary>
public interface IPathPolicy {
    bool IsAllowed(string path);

    /// <summary>
    /// Canonicalizes <paramref name="path"/> and throws if it is outside the
    /// allowed roots or cannot be resolved safely (unresolvable reparse point).
    /// Returns the canonical path.
    /// </summary>
    string EnsureAllowed(string path);

    bool TryResolveWithinProject(string projectPath, string relativePath, out string targetPath);

    bool IsSecretName(string path);

    bool ExceedsMaxSize(long byteLength);
}
