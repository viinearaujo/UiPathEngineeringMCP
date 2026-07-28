namespace UiPath.Engineering.Mcp.Providers.UiPathCli;

/// <summary>
/// Resolves the configured CLI executable (UiPathCliOptions.ExecutablePath)
/// to a launch spec Process.Start can use. The UiPath CLI is typically installed via npm,
/// which puts uip / uip.cmd / uip.ps1 shims on PATH (no uip.exe), so a bare command name is
/// probed against every PATH directory with several extensions, and script shims are launched
/// through their host (cmd.exe / powershell.exe).
/// </summary>
internal static class CliExecutableResolver {
    public sealed record LaunchSpec(string FileName, string ArgumentPrefix, string ArgumentSuffix, string ResolvedPath);

    // Probe priority when the configured value is a bare command name.
    private static readonly string[] ProbeExtensions = [".exe", ".cmd", ".bat", ".ps1"];

    public static LaunchSpec? Resolve(string configuredName) =>
        Resolve(
            configuredName,
            (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries),
            File.Exists);

    // Pure core, split out for testing: no environment or filesystem access.
    internal static LaunchSpec? Resolve(
        string configuredName,
        IEnumerable<string> pathDirectories,
        Func<string, bool> fileExists) {
        if (string.IsNullOrWhiteSpace(configuredName)) {
            return null;
        }

        // 1. An explicit path (or bare file in the working directory) that exists: use as-is.
        if (fileExists(configuredName)) {
            return ToLaunchSpec(configuredName);
        }

        // 2. Bare command name: probe each PATH directory for candidates in priority order.
        //    If the configured name carries an extension that is not found (e.g. the legacy
        //    default "uip.exe"), fall back to the other extensions of the same base name so
        //    existing configurations keep working with the npm shims.
        var candidates = BuildCandidates(configuredName);
        foreach (var directory in pathDirectories) {
            if (string.IsNullOrWhiteSpace(directory)) {
                continue;
            }

            foreach (var candidate in candidates) {
                var fullPath = Path.Combine(directory, candidate);
                if (fileExists(fullPath)) {
                    return ToLaunchSpec(fullPath);
                }
            }
        }

        return null;
    }

    internal static List<string> BuildCandidates(string configuredName) {
        var candidates = new List<string>();
        if (!Path.HasExtension(configuredName)) {
            candidates.AddRange(ProbeExtensions.Select(ext => configuredName + ext));
            candidates.Add(configuredName);
        } else {
            var baseName = Path.GetFileNameWithoutExtension(configuredName);
            candidates.Add(configuredName);
            candidates.AddRange(ProbeExtensions
                .Select(ext => baseName + ext)
                .Where(c => !string.Equals(c, configuredName, StringComparison.OrdinalIgnoreCase)));
            candidates.Add(baseName);
        }
        return candidates;
    }

    private static LaunchSpec ToLaunchSpec(string resolvedPath) =>
        Path.GetExtension(resolvedPath).ToLowerInvariant() switch {
            // cmd.exe swallows the first and last quote of the /c command line, so the
            // whole line must be wrapped in an extra pair of quotes (verified live:
            // /c ""<shim>" restore "<proj>"" works, /c "<shim>" restore "<proj>" fails).
            // Argument prefixes end with a space so callers can append their arguments
            // directly; the suffix (if any) closes the doubled outer quotes.
            ".cmd" or ".bat" => new LaunchSpec("cmd.exe", $"/c \"\"{resolvedPath}\" ", "\"", resolvedPath),
            ".ps1" => new LaunchSpec("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -File \"{resolvedPath}\" ", "", resolvedPath),
            _ => new LaunchSpec(resolvedPath, string.Empty, string.Empty, resolvedPath)
        };
}
