namespace UiPath.Engineering.Mcp.Providers.Skills;

// Resolves the configured skills root. Absolute paths pass through; relative
// paths are searched by walking up from a start directory (server working
// directory, then the app base directory) so the same relative config works
// regardless of where the server process is launched from.
public static class SkillsRootResolver {
    public static string Resolve(string configuredRoot, string startDirectory) {
        if (Path.IsPathRooted(configuredRoot)) {
            return Path.GetFullPath(configuredRoot);
        }

        for (var dir = Path.GetFullPath(startDirectory); dir is not null; dir = Path.GetDirectoryName(dir)) {
            var candidate = Path.GetFullPath(Path.Combine(dir, configuredRoot));
            if (Directory.Exists(candidate)) {
                return candidate;
            }
        }

        return Path.GetFullPath(Path.Combine(startDirectory, configuredRoot));
    }
}
