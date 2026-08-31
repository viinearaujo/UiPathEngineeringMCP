using System.Security.Cryptography;
using System.Text;
using UiPath.Engineering.Mcp.Core.Abstractions;

namespace UiPath.Engineering.Mcp.Core.Caching;

/// <summary>
/// SHA-256 of sorted path + last-write ticks. Shared by the project-model cache
/// and the C# analysis cache (the latter appends NuGet folder ticks).
/// </summary>
public static class ProjectFingerprint {
    public const string StaleCacheWarning =
        "Fingerprint could not be computed; serving a possibly stale cached result.";

    public static bool TryComputeProjectFiles(
        IFilesystemProvider filesystem,
        string projectPath,
        out string fingerprint) {
        fingerprint = string.Empty;
        try {
            var files = filesystem.FindXamlFiles(projectPath)
                .Concat(filesystem.FindCSharpFiles(projectPath))
                .ToList();
            var projectJson = filesystem.FindProjectJson(projectPath);
            if (projectJson is not null) {
                files.Add(projectJson);
            }

            fingerprint = Hash(CollectTicks(filesystem, files));
            return true;
        } catch (Exception ex) when (IsIoFailure(ex)) {
            return false;
        }
    }

    public static bool TryCompute(
        IFilesystemProvider filesystem,
        IEnumerable<string> files,
        out string fingerprint,
        IEnumerable<(string Path, long Ticks)>? extraTicks = null) {
        fingerprint = string.Empty;
        try {
            var pairs = CollectTicks(filesystem, files);
            if (extraTicks is not null) {
                pairs.AddRange(extraTicks);
            }

            fingerprint = Hash(pairs);
            return true;
        } catch (Exception ex) when (IsIoFailure(ex)) {
            return false;
        }
    }

    public static string Hash(IEnumerable<(string Path, long Ticks)> pairs) {
        var sb = new StringBuilder();
        foreach (var (path, ticks) in pairs.OrderBy(p => p.Path, StringComparer.OrdinalIgnoreCase)) {
            sb.Append(path).Append('\0').Append(ticks).Append('\n');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    public static void AddStaleWarning(List<string> warnings, bool stale) {
        if (stale) {
            warnings.Add(StaleCacheWarning);
        }
    }

    public static bool IsIoFailure(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or FileNotFoundException or DirectoryNotFoundException;

    private static List<(string Path, long Ticks)> CollectTicks(IFilesystemProvider filesystem, IEnumerable<string> files) {
        var pairs = new List<(string Path, long Ticks)>();
        foreach (var file in files) {
            pairs.Add((file, filesystem.GetLastWriteTimeUtc(file).Ticks));
        }

        return pairs;
    }
}
