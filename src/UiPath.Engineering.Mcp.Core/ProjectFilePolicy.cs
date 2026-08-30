using System.Text.Json;

namespace UiPath.Engineering.Mcp.Core;

/// <summary>
/// Safety rules for <c>manage_project_file</c>: extension allowlist, reserved paths,
/// secret-looking names, redacted-body refusal, and JSON parse for <c>.json</c>.
/// </summary>
public static class ProjectFilePolicy {
    public static readonly string[] AllowedExtensions = [".md", ".json", ".txt"];
    public const string RedactedMarker = "***REDACTED***";

    private static readonly string[] ReservedExact = [
        "project.json",
        "docs/implementation-plan.json",
        "docs/implementation-plan.md"
    ];

    private static readonly string[] ReservedPrefixes = [
        "docs/knowledge/",
        "docs/adr/"
    ];

    private static readonly string[] SecretExtensions = [".pem", ".key"];

    public static string NormalizeRelativePath(string relativePath) {
        var normalized = relativePath.Replace('\\', '/').Trim();
        while (normalized.StartsWith("./", StringComparison.Ordinal)) {
            normalized = normalized[2..];
        }

        return normalized.TrimStart('/');
    }

    public static bool IsAllowedExtension(string relativePath) {
        var extension = Path.GetExtension(NormalizeRelativePath(relativePath));
        return AllowedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }

    public static bool IsSecretName(string relativePath) {
        var normalized = NormalizeRelativePath(relativePath);
        var fileName = Path.GetFileName(normalized);
        var extension = Path.GetExtension(normalized);
        return fileName.StartsWith(".env", StringComparison.OrdinalIgnoreCase)
            || fileName.Contains("credentials", StringComparison.OrdinalIgnoreCase)
            || SecretExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }

    public static bool IsReservedPath(string relativePath) {
        var normalized = NormalizeRelativePath(relativePath);
        if (ReservedExact.Contains(normalized, StringComparer.OrdinalIgnoreCase)) {
            return true;
        }

        return ReservedPrefixes.Any(prefix =>
            normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    public static bool ContainsRedactedBody(string? content) =>
        content is not null && content.Contains(RedactedMarker, StringComparison.Ordinal);

    public static bool TryParseJson(string content, out string? error) {
        try {
            using var _ = JsonDocument.Parse(content);
            error = null;
            return true;
        } catch (JsonException ex) {
            error = ex.Message;
            return false;
        }
    }

    public static string? ValidateMutatingFile(string relativePath, string? content, bool requireContent) {
        if (string.IsNullOrWhiteSpace(relativePath)) {
            return "relativePath is required.";
        }

        if (!IsAllowedExtension(relativePath)) {
            return $"Only {string.Join(", ", AllowedExtensions)} files can be written; got '{Path.GetExtension(relativePath)}'.";
        }

        if (IsReservedPath(relativePath)) {
            return $"'{NormalizeRelativePath(relativePath)}' is owned by another tool and cannot be changed with manage_project_file.";
        }

        if (IsSecretName(relativePath)) {
            return $"'{relativePath}' looks like a secret or key file and cannot be written.";
        }

        if (requireContent && content is null) {
            return "content is required.";
        }

        if (ContainsRedactedBody(content)) {
            return "content contains ***REDACTED*** and must not be written back to disk.";
        }

        var normalized = NormalizeRelativePath(relativePath);
        if (requireContent
            && normalized.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            && content is not null
            && !TryParseJson(content, out var jsonError)) {
            return $"content is not valid JSON: {jsonError}";
        }

        return null;
    }

    public static bool IsWithinProject(string projectPath, string candidate) {
        var root = Path.GetFullPath(projectPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string full;
        try {
            full = Path.GetFullPath(candidate)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        } catch {
            return false;
        }

        if (string.Equals(root, full, StringComparison.OrdinalIgnoreCase)) {
            return true;
        }

        return full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    public static string CombineProject(string projectPath, string relativePath) =>
        Path.Combine(Path.GetFullPath(projectPath), NormalizeRelativePath(relativePath).Replace('/', Path.DirectorySeparatorChar));
}
