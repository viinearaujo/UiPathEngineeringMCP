using UiPath.Engineering.Mcp.Core.Configuration;

namespace UiPath.Engineering.Mcp.Core.Knowledge;

/// <summary>
/// Locates the corpora the knowledge-search tools read: the vendored uipath-rpa
/// <c>references/</c> tree (activity docs plus the guide corpus), the shipped Copilot
/// idiom samples, and the project-local <c>{PROJECT_DIR}/.local/docs/packages</c> tree.
/// </summary>
/// <remarks>
/// Every resolver walks up from the supplied start directory (defaulting to the
/// server's working directory, then <see cref="AppContext.BaseDirectory"/>) so the same
/// relative configuration works from a dev build and a published output. A missing
/// corpus returns null rather than throwing: a tool degrades to whichever corpora are
/// present and reports which ones it read.
/// </remarks>
public static class KnowledgePaths {
    /// <summary>Activity-doc corpus, relative to the configured skills root.</summary>
    public const string ActivityDocsUnderSkillsRoot = "uipath-rpa/references/activity-docs";

    /// <summary>Guide corpus, relative to the configured skills root.</summary>
    public const string ReferenceGuidesUnderSkillsRoot = "uipath-rpa/references";

    /// <summary>Project-local package docs, relative to the project root.</summary>
    public const string ProjectActivityDocsRelative = ".local/docs/packages";

    public static string? ResolveActivityDocsRoot(SkillsOptions options, string? startDirectory = null) =>
        ResolveUnderSkillsRoot(options.SkillsRoot, ActivityDocsUnderSkillsRoot, startDirectory);

    public static string? ResolveReferenceGuidesRoot(SkillsOptions options, string? startDirectory = null) =>
        ResolveUnderSkillsRoot(options.SkillsRoot, ReferenceGuidesUnderSkillsRoot, startDirectory);

    public static string? ResolveCopilotIdiomsRoot(SkillsOptions options, string? startDirectory = null) =>
        ResolveConfiguredRoot(options.CopilotIdiomsRoot, startDirectory);

    /// <summary>
    /// The project-local package-docs tree, or null when the project has none (no installed
    /// package ships docs, or <c>.local</c> was never created). Resolved through
    /// <see cref="PathPolicy.TryResolveProjectRelative"/>, which canonicalizes (following
    /// reparse points) and refuses anything not landing inside the already-allowed project
    /// root — the same sandbox <c>CSharpContextBuilder</c> uses for generated sources.
    /// </summary>
    public static string? ResolveProjectActivityDocsRoot(string? projectPath) {
        if (string.IsNullOrWhiteSpace(projectPath)
            || !PathPolicy.TryResolveProjectRelative(projectPath, ProjectActivityDocsRelative, out var root)
            || !Directory.Exists(root)) {
            return null;
        }

        return root;
    }

    public static string? ResolveUnderSkillsRoot(string skillsRoot, string relative, string? startDirectory = null) {
        if (string.IsNullOrWhiteSpace(skillsRoot) || string.IsNullOrWhiteSpace(relative)) {
            return null;
        }

        var combined = Combine(skillsRoot, relative);
        return Path.IsPathRooted(combined)
            ? ExistingDirectory(combined)
            : WalkUp(combined, startDirectory);
    }

    private static string? ResolveConfiguredRoot(string configuredRoot, string? startDirectory) {
        if (string.IsNullOrWhiteSpace(configuredRoot)) {
            return null;
        }

        return Path.IsPathRooted(configuredRoot)
            ? ExistingDirectory(configuredRoot)
            : WalkUp(configuredRoot, startDirectory);
    }

    // A configured relative path is probed against each ancestor of the start directory:
    // the explicit start directory first, then the working directory, then the app base
    // directory. Mirrors SkillsRootResolver so one relative value works everywhere.
    private static string? WalkUp(string relative, string? startDirectory) {
        foreach (var start in StartDirectories(startDirectory)) {
            var found = WalkUpFrom(relative, start);
            if (found is not null) {
                return found;
            }
        }

        return null;
    }

    private static string? WalkUpFrom(string relative, string startDirectory) {
        var normalized = relative.Replace('/', Path.DirectorySeparatorChar);
        string? dir;
        try {
            dir = Path.GetFullPath(startDirectory);
        } catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) {
            return null;
        }

        while (!string.IsNullOrEmpty(dir)) {
            var candidate = ExistingDirectory(Path.Combine(dir, normalized));
            if (candidate is not null) {
                return candidate;
            }

            dir = Path.GetDirectoryName(dir);
        }

        return null;
    }

    private static IEnumerable<string> StartDirectories(string? startDirectory) {
        if (!string.IsNullOrWhiteSpace(startDirectory)) {
            yield return startDirectory!;
        }

        var current = SafeCurrentDirectory();
        if (current is not null) {
            yield return current;
        }

        yield return AppContext.BaseDirectory;
    }

    private static string? SafeCurrentDirectory() {
        try {
            return Directory.GetCurrentDirectory();
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return null;
        }
    }

    private static string Combine(string first, string second) =>
        Path.Combine(first.Replace('/', Path.DirectorySeparatorChar), second.Replace('/', Path.DirectorySeparatorChar));

    private static string? ExistingDirectory(string path) {
        string full;
        try {
            full = Path.GetFullPath(path);
        } catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) {
            return null;
        }

        return Directory.Exists(full) ? full : null;
    }
}