using System.Text.RegularExpressions;
using UiPath.Engineering.Mcp.Core.Models;

namespace UiPath.Engineering.Mcp.Core.CodeAnalysis;

/// <summary>
/// Maps project.json dependencies to assembly file paths in the NuGet global-packages
/// folder, plus framework reference assemblies from the machine's .NET targeting packs
/// (falling back to the server's own runtime assemblies for modern targets).
/// </summary>
public class NuGetReferenceResolver {
    private readonly string? _packagesFolderOverride;

    // packagesFolderOverride exists for tests; production uses the default probing.
    public NuGetReferenceResolver(string? packagesFolderOverride = null) {
        _packagesFolderOverride = packagesFolderOverride;
    }

    public virtual string? GetPackagesFolder() {
        var folder = _packagesFolderOverride
            ?? Environment.GetEnvironmentVariable("NUGET_PACKAGES")
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".nuget", "packages");
        return Directory.Exists(folder) ? folder : null;
    }

    public ReferenceResolution Resolve(IReadOnlyList<PackageModel> packages, string? targetFramework) {
        var result = new ReferenceResolution();
        var paths = new List<string>();

        var (frameworkPaths, frameworkResolved) = ResolveFrameworkAssemblies(targetFramework);
        paths.AddRange(frameworkPaths);
        result.FrameworkResolved = frameworkResolved;

        var packagesFolder = GetPackagesFolder();
        result.PackagesFolderFound = packagesFolder is not null;

        foreach (var package in packages) {
            var packagePaths = packagesFolder is null
                ? []
                : ResolvePackageAssemblies(packagesFolder, package, targetFramework);
            if (packagePaths.Count == 0) {
                result.UnresolvedDependencies.Add(package.Id);
            } else {
                paths.AddRange(packagePaths);
            }
        }

        result.AssemblyPaths = DeduplicateByFileName(paths);
        return result;
    }

    // --- Framework assemblies -------------------------------------------------

    internal static (List<string> Paths, bool Resolved) ResolveFrameworkAssemblies(string? targetFramework) {
        var tfm = NormalizeTfm(targetFramework);
        return IsLegacyTfm(tfm) ? ResolveLegacyFramework(tfm) : ResolveModernFramework(tfm);
    }

    public static string NormalizeTfm(string? targetFramework) {
        if (string.IsNullOrWhiteSpace(targetFramework)) {
            return "net8.0";
        }
        var tfm = targetFramework.Trim().ToLowerInvariant();
        // UiPath writes values like "net6.0" or "net6.0-windows"; strip platform suffixes.
        var dash = tfm.IndexOf('-');
        return dash > 0 ? tfm[..dash] : tfm;
    }

    private static bool IsLegacyTfm(string tfm) => Regex.IsMatch(tfm, @"^net4\d{1,2}$");

    private static (List<string> Paths, bool Resolved) ResolveModernFramework(string tfm) {
        var packsRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "dotnet", "packs", "Microsoft.NETCore.App.Ref");
        if (Directory.Exists(packsRoot)) {
            var match = Directory.GetDirectories(packsRoot)
                .Select(versionDir => Path.Combine(versionDir, "ref", tfm))
                .Where(Directory.Exists)
                .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            if (match is not null) {
                return (Directory.GetFiles(match, "*.dll").ToList(), true);
            }
        }

        // Fallback: the server's own runtime assemblies are valid compile references.
        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location);
        return runtimeDir is not null
            ? (Directory.GetFiles(runtimeDir, "*.dll").ToList(), true)
            : ([], false);
    }

    private static (List<string> Paths, bool Resolved) ResolveLegacyFramework(string tfm) {
        var version = tfm switch {
            "net48" => "4.8",
            "net472" => "4.7.2",
            "net471" => "4.7.1",
            "net47" => "4.7",
            "net462" => "4.6.2",
            "net461" => "4.6.1",
            "net46" => "4.6",
            "net45" => "4.5",
            _ => null
        };
        if (version is null) {
            return ([], false);
        }

        var refDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Reference Assemblies", "Microsoft", "Framework", ".NETFramework", "v" + version);
        return Directory.Exists(refDir)
            ? (Directory.GetFiles(refDir, "*.dll").ToList(), true)
            : ([], false);
    }

    // --- Package assemblies ---------------------------------------------------

    private static List<string> ResolvePackageAssemblies(string packagesFolder, PackageModel package, string? targetFramework) {
        var idFolder = Path.Combine(packagesFolder, package.Id.ToLowerInvariant());
        if (!Directory.Exists(idFolder)) {
            return [];
        }

        var versionFolder = SelectVersionFolder(idFolder, package.Version);
        if (versionFolder is null) {
            return [];
        }

        var tfm = NormalizeTfm(targetFramework);
        foreach (var group in new[] { "ref", "lib" }) {
            var groupDir = Path.Combine(versionFolder, group);
            if (!Directory.Exists(groupDir)) {
                continue;
            }
            var bestTfmDir = SelectBestTfmFolder(groupDir, tfm);
            if (bestTfmDir is not null) {
                return Directory.GetFiles(bestTfmDir, "*.dll").ToList();
            }
        }

        return [];
    }

    internal static string? SelectVersionFolder(string idFolder, string wantedVersion) {
        var dirs = Directory.GetDirectories(idFolder);
        var exact = dirs.FirstOrDefault(d =>
            string.Equals(Path.GetFileName(d), wantedVersion, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) {
            return exact;
        }

        return dirs
            .Select(d => (Dir: d, Version: ParseVersion(Path.GetFileName(d))))
            .Where(x => x.Version is not null)
            .OrderByDescending(x => x.Version)
            .Select(x => x.Dir)
            .FirstOrDefault();
    }

    private static Version? ParseVersion(string text) =>
        Version.TryParse(text.Split('-')[0], out var version) ? version : null;

    internal static string? SelectBestTfmFolder(string groupDir, string targetTfm) =>
        Directory.GetDirectories(groupDir)
            .Select(d => (Dir: d, Score: ScoreTfmFolder(Path.GetFileName(d), targetTfm)))
            .Where(x => x.Score >= 0)
            .OrderByDescending(x => x.Score)
            .Select(x => x.Dir)
            .FirstOrDefault();

    /// <summary>
    /// Scores a package's ref/lib TFM folder against the project target.
    /// Higher is better; -1 means incompatible. Exact match = 1000.
    /// For modern targets: older netX.Y folders rank by version, netstandard below them.
    /// For legacy (net4xx) targets: only net4xx folders qualify.
    /// </summary>
    public static int ScoreTfmFolder(string folderName, string targetTfm) {
        var name = folderName.ToLowerInvariant();
        var target = targetTfm.ToLowerInvariant();
        if (name == target) {
            return 1000;
        }

        if (IsLegacyTfm(target)) {
            return IsLegacyTfm(name) && TfmVersionRank(name) <= TfmVersionRank(target)
                ? TfmVersionRank(name)
                : -1;
        }

        if (Regex.IsMatch(name, @"^net\d+\.\d+$")) {
            var rank = TfmVersionRank(name);
            return rank <= TfmVersionRank(target) ? rank : -1;
        }

        if (Regex.IsMatch(name, @"^netstandard\d+(\.\d+)?$")) {
            // netstandard is usable from net5+ and always ranks below netX.Y folders.
            var rank = TfmVersionRank(name);
            return rank <= 21 ? rank : -1;
        }

        return -1;
    }

    // "net6.0" -> 60, "netstandard2.0" -> 20, "net461" -> 461.
    private static int TfmVersionRank(string tfm) {
        var dotted = Regex.Match(tfm, @"\d+\.\d+");
        if (dotted.Success) {
            var parts = dotted.Value.Split('.');
            return int.Parse(parts[0]) * 10 + int.Parse(parts[1]);
        }
        // Legacy net4x/net4xx forms must share one scale: net45 -> 450, net48 -> 480,
        // net461 -> 461, net472 -> 472, so e.g. net461 <= net48 but net48 > net472.
        var legacy = Regex.Match(tfm, @"^net(4\d\d?)$");
        if (legacy.Success) {
            var value = int.Parse(legacy.Groups[1].Value);
            return value < 100 ? value * 10 : value;
        }
        var plain = Regex.Match(tfm, @"\d+");
        return plain.Success ? int.Parse(plain.Value) : 0;
    }

    // Keeps the first occurrence of each file name (framework paths come first),
    // preventing CS1703 duplicate-reference noise from packages shipping facades.
    private static List<string> DeduplicateByFileName(List<string> paths) {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var path in paths) {
            if (seen.Add(Path.GetFileName(path))) {
                result.Add(path);
            }
        }
        return result;
    }
}
