using System.Security;
using UiPath.Engineering.Mcp.Core.Abstractions;
using UiPath.Engineering.Mcp.Core.Configuration;

namespace UiPath.Engineering.Mcp.Core;

/// <summary>
/// Canonicalizes via <see cref="Path.GetFullPath"/> plus <see cref="FileSystemInfo"/>
/// reparse resolution. Unresolvable reparse points are rejected rather than followed
/// lexically, so a junction under a project cannot escape the allowlist.
/// </summary>
public sealed class PathPolicy : IPathPolicy {
    private static readonly string[] SecretExtensions = [".pem", ".key"];

    private readonly string[] _allowedRoots;

    public PathPolicy(ProjectRootOptions options)
        : this(options.AllowedRoots) {
    }

    public PathPolicy(IEnumerable<string>? allowedRoots) {
        _allowedRoots = allowedRoots?
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .ToArray() ?? [];
    }

    public bool IsAllowed(string path) {
        if (!TryCanonicalize(path, out var canonical)) {
            return false;
        }

        return IsWithinAnyRoot(canonical);
    }

    public string EnsureAllowed(string path) {
        if (!TryCanonicalize(path, out var canonical) || !IsWithinAnyRoot(canonical)) {
            throw new UnauthorizedAccessException($"Path is outside the configured allowed roots: {path}");
        }

        return canonical;
    }

    public bool TryResolveWithinProject(string projectPath, string relativePath, out string targetPath) =>
        TryResolveProjectRelative(projectPath, relativePath, out targetPath);

    public bool IsSecretName(string path) => LooksLikeSecret(path);

    public bool ExceedsMaxSize(long byteLength) => byteLength > FileReadLimits.MaxFileBytes;

    public static bool LooksLikeSecret(string path) {
        if (string.IsNullOrWhiteSpace(path)) {
            return false;
        }

        var fileName = Path.GetFileName(path.Replace('\\', '/'));
        var extension = Path.GetExtension(fileName);
        return fileName.StartsWith(".env", StringComparison.OrdinalIgnoreCase)
            || fileName.Contains("credentials", StringComparison.OrdinalIgnoreCase)
            || SecretExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }

    public static bool IsWithin(string root, string candidate, bool allowEqual = true) {
        if (!TryCanonicalize(root, out var canonicalRoot)
            || !TryCanonicalize(candidate, out var canonicalCandidate)) {
            return false;
        }

        return HasPrefixBoundary(canonicalRoot, canonicalCandidate, allowEqual);
    }

    public static bool TryResolveProjectRelative(string projectPath, string relativePath, out string targetPath) {
        targetPath = string.Empty;
        if (string.IsNullOrWhiteSpace(relativePath)) {
            return false;
        }

        if (!TryCanonicalize(projectPath, out var project)) {
            return false;
        }

        var normalized = ProjectFilePolicy.NormalizeRelativePath(relativePath);
        string combined;
        try {
            combined = Path.Combine(project, normalized.Replace('/', Path.DirectorySeparatorChar));
        } catch (ArgumentException) {
            return false;
        }

        if (!TryCanonicalize(combined, out var canonical)
            || !HasPrefixBoundary(project, canonical, allowEqual: false)) {
            return false;
        }

        targetPath = canonical;
        return true;
    }

    public static bool TryCanonicalize(string path, out string canonical) {
        canonical = string.Empty;
        if (string.IsNullOrWhiteSpace(path)) {
            return false;
        }

        string full;
        try {
            full = Path.GetFullPath(path);
        } catch (Exception ex) when (
            ex is ArgumentException
            or NotSupportedException
            or PathTooLongException) {
            return false;
        }

        var root = Path.GetPathRoot(full);
        if (string.IsNullOrEmpty(root)) {
            return false;
        }

        var relative = full[root.Length..];
        var segments = relative.Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);

        var current = root;
        foreach (var segment in segments) {
            current = Path.Combine(current, segment);
            if (!TryResolveExistingNode(current, out current)) {
                return false;
            }
        }

        try {
            canonical = Path.GetFullPath(current);
        } catch (Exception ex) when (
            ex is ArgumentException
            or NotSupportedException
            or PathTooLongException) {
            return false;
        }

        return true;
    }

    public static string SecretReadRefusal(string relativePath) =>
        $"'{relativePath}' looks like a secret or key file and cannot be read.";

    private bool IsWithinAnyRoot(string canonicalPath) {
        foreach (var root in _allowedRoots) {
            if (!TryCanonicalize(root, out var canonicalRoot)) {
                continue;
            }

            if (HasPrefixBoundary(canonicalRoot, canonicalPath, allowEqual: true)) {
                return true;
            }
        }

        return false;
    }

    private static bool HasPrefixBoundary(string root, string candidate, bool allowEqual) {
        var normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedCandidate = candidate.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (string.Equals(normalizedRoot, normalizedCandidate, StringComparison.OrdinalIgnoreCase)) {
            return allowEqual;
        }

        return normalizedCandidate.StartsWith(
            normalizedRoot + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
    }

    // Existing prefixes: GetAttributes + ResolveLinkTarget when the node is a
    // reparse point. Missing nodes stay lexical so new files can still be written.
    // Anything we cannot inspect or resolve is rejected.
    private static bool TryResolveExistingNode(string path, out string resolved) {
        resolved = path;
        FileAttributes attrs;
        try {
            attrs = File.GetAttributes(path);
        } catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) {
            return true;
        } catch (Exception ex) when (
            ex is IOException
            or UnauthorizedAccessException
            or SecurityException
            or NotSupportedException) {
            return false;
        }

        if ((attrs & FileAttributes.ReparsePoint) == 0) {
            return true;
        }

        FileSystemInfo info = (attrs & FileAttributes.Directory) != 0
            ? new DirectoryInfo(path)
            : new FileInfo(path);

        FileSystemInfo? target;
        try {
            target = info.ResolveLinkTarget(returnFinalTarget: true);
        } catch (Exception ex) when (
            ex is IOException
            or UnauthorizedAccessException
            or SecurityException
            or NotSupportedException
            or PlatformNotSupportedException) {
            return false;
        }

        if (target is null || string.IsNullOrWhiteSpace(target.FullName)) {
            return false;
        }

        try {
            resolved = Path.GetFullPath(target.FullName);
        } catch (Exception ex) when (
            ex is ArgumentException
            or NotSupportedException
            or PathTooLongException) {
            return false;
        }

        return true;
    }
}
