using System.Xml.Linq;
using UiPath.Engineering.Mcp.Core.Abstractions;
using UiPath.Engineering.Mcp.Core.Authoring;
using UiPath.Engineering.Mcp.Core.Models;

namespace UiPath.Engineering.Mcp.Core.Parsing;

/// <summary>
/// Maps CLI validate/build diagnostics onto snapshot activity IDs (from
/// <see cref="XamlActivityLocator"/>) and catalog spec property names.
/// </summary>
public static class ValidateDiagnosticMapper {
    public static IReadOnlyList<ValidateFixDiagnostic> Map(
        string projectPath,
        IFilesystemProvider filesystem,
        IEnumerable<CliDiagnostic> diagnostics) {
        var cache = new Dictionary<string, IReadOnlyList<LocatedActivity>>(StringComparer.OrdinalIgnoreCase);
        var mapped = new List<ValidateFixDiagnostic>();
        foreach (var diagnostic in diagnostics) {
            mapped.Add(MapOne(projectPath, filesystem, diagnostic, cache));
        }

        return mapped;
    }

    private static ValidateFixDiagnostic MapOne(
        string projectPath,
        IFilesystemProvider filesystem,
        CliDiagnostic diagnostic,
        Dictionary<string, IReadOnlyList<LocatedActivity>> cache) {
        var xamlPath = ResolveXamlPath(projectPath, diagnostic.FilePath, filesystem);
        LocatedActivity? located = null;
        string? workflowFile = WorkflowFileName(projectPath, xamlPath, diagnostic.FilePath);

        if (xamlPath is not null) {
            var activities = LocateCached(filesystem, xamlPath, cache);
            located = ResolveActivity(activities, diagnostic);
        }

        var activityType = located is null
            ? null
            : StripGenericSuffix(located.Element.Name.LocalName);
        var property = CanonicalProperty(activityType, diagnostic.Property);
        var specFix = BuildSpecFix(located, workflowFile, activityType, property, diagnostic);

        return new ValidateFixDiagnostic {
            ActivityId = located?.Id,
            Property = property,
            Message = diagnostic.Message,
            SpecFix = specFix
        };
    }

    private static LocatedActivity? ResolveActivity(
        IReadOnlyList<LocatedActivity> activities, CliDiagnostic diagnostic) {
        if (activities.Count == 0) {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(diagnostic.IdRef)) {
            var byRef = activities
                .Where(a => string.Equals(a.IdRef, diagnostic.IdRef, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (byRef.Count == 1) {
                return byRef[0];
            }

            if (byRef.Count > 1) {
                return Disambiguate(byRef, diagnostic) ?? byRef[0];
            }
        }

        if (!string.IsNullOrWhiteSpace(diagnostic.DisplayName)) {
            var byName = activities
                .Where(a => string.Equals(
                    a.Element.Attribute("DisplayName")?.Value,
                    diagnostic.DisplayName,
                    StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (byName.Count == 1) {
                return byName[0];
            }

            if (byName.Count > 1) {
                return Disambiguate(byName, diagnostic) ?? byName[0];
            }
        }

        return MatchByLine(activities, diagnostic.Line);
    }

    private static LocatedActivity? Disambiguate(
        List<LocatedActivity> candidates, CliDiagnostic diagnostic) =>
        diagnostic.Line is int line
            ? MatchByLine(candidates, line)
            : null;

    private static LocatedActivity? MatchByLine(IReadOnlyList<LocatedActivity> activities, int? line) {
        if (line is not int target || target <= 0) {
            return null;
        }

        var exact = activities.Where(a => a.Line == target).ToList();
        if (exact.Count == 1) {
            return exact[0];
        }

        if (exact.Count > 1) {
            return exact.OrderByDescending(a => a.Depth).First();
        }

        return activities
            .Where(a => a.Line > 0 && a.Line <= target)
            .OrderByDescending(a => a.Line)
            .ThenByDescending(a => a.Depth)
            .FirstOrDefault();
    }

    private static IReadOnlyList<LocatedActivity> LocateCached(
        IFilesystemProvider filesystem,
        string xamlPath,
        Dictionary<string, IReadOnlyList<LocatedActivity>> cache) {
        if (cache.TryGetValue(xamlPath, out var cached)) {
            return cached;
        }

        IReadOnlyList<LocatedActivity> located;
        try {
            var content = filesystem.ReadAllText(xamlPath);
            var doc = XDocument.Parse(content, LoadOptions.SetLineInfo);
            located = XamlActivityLocator.Locate(doc);
        } catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException
            or System.Xml.XmlException or InvalidOperationException or UnauthorizedAccessException) {
            located = [];
        }

        cache[xamlPath] = located;
        return located;
    }

    private static string? ResolveXamlPath(
        string projectPath, string? filePath, IFilesystemProvider filesystem) {
        if (string.IsNullOrWhiteSpace(filePath)) {
            return null;
        }

        var name = Path.GetFileName(filePath);
        foreach (var xaml in filesystem.FindXamlFiles(projectPath)) {
            if (string.Equals(Path.GetFileName(xaml), name, StringComparison.OrdinalIgnoreCase)) {
                return xaml;
            }

            var normalizedXaml = xaml.Replace('\\', '/');
            var normalizedFile = filePath.Replace('\\', '/');
            if (normalizedXaml.EndsWith(normalizedFile, StringComparison.OrdinalIgnoreCase)
                || normalizedXaml.EndsWith('/' + name, StringComparison.OrdinalIgnoreCase)) {
                return xaml;
            }
        }

        var combined = Path.IsPathRooted(filePath)
            ? filePath
            : Path.Combine(projectPath, filePath.Replace('/', Path.DirectorySeparatorChar));
        if (filesystem.FileExists(combined)) {
            return combined;
        }

        if (filesystem.FileExists(filePath)) {
            return filePath;
        }

        return null;
    }

    private static string? WorkflowFileName(string projectPath, string? resolvedPath, string? original) {
        if (!string.IsNullOrWhiteSpace(original) && original.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)
            && !Path.IsPathRooted(original)) {
            return original.Replace('\\', '/');
        }

        var source = resolvedPath ?? original;
        if (string.IsNullOrWhiteSpace(source)) {
            return null;
        }

        try {
            var relative = Path.GetRelativePath(projectPath, source);
            if (!relative.StartsWith("..", StringComparison.Ordinal)) {
                return relative.Replace('\\', '/');
            }
        } catch (ArgumentException) {
            // Fake unix-style paths on Windows can fail GetRelativePath.
        }

        return Path.GetFileName(source);
    }

    private static string? CanonicalProperty(string? activityType, string? property) {
        if (string.IsNullOrWhiteSpace(property)) {
            return null;
        }

        var name = property.Trim();
        var lastDot = name.LastIndexOf('.');
        if (lastDot >= 0 && lastDot < name.Length - 1) {
            name = name[(lastDot + 1)..];
        }

        if (name.Equals("TypeArguments", StringComparison.OrdinalIgnoreCase)) {
            name = "TypeArgument";
        }

        if (activityType is not null && ActivityCatalog.TryGet(activityType, out var schema)) {
            var match = schema.Properties.FirstOrDefault(p =>
                string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
            if (match is not null) {
                return match.Name;
            }
        }

        return name;
    }

    private static string StripGenericSuffix(string localName) {
        var tick = localName.IndexOf('`');
        return tick >= 0 ? localName[..tick] : localName;
    }

    private static SpecFixSuggestion? BuildSpecFix(
        LocatedActivity? located,
        string? workflowFile,
        string? activityType,
        string? property,
        CliDiagnostic diagnostic) {
        Dictionary<string, string?>? properties = null;
        if (property is not null) {
            properties = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) {
                [property] = ReadPropertyValue(located?.Element, property)
            };
        }

        var hint = diagnostic.Recommendation
            ?? DefaultHint(located?.Id, workflowFile, property, activityType);
        if (properties is null && string.IsNullOrWhiteSpace(hint) && string.IsNullOrWhiteSpace(workflowFile)) {
            return null;
        }

        return new SpecFixSuggestion {
            WorkflowFile = workflowFile,
            Properties = properties,
            Hint = hint
        };
    }

    private static string? DefaultHint(
        string? activityId, string? workflowFile, string? property, string? activityType) {
        var target = activityId is not null
            ? $"activity '{activityId}'"
            : "the failing activity";
        var file = workflowFile is not null ? $" in {workflowFile}" : "";
        if (property is not null) {
            var form = PropertyFormHint(activityType, property);
            return $"Set '{property}' on {target}{file}{form}. Pass activityId to edit_workflow_activity / insert_activities, then re-run validate_project.";
        }

        if (activityId is not null || workflowFile is not null) {
            return $"Inspect {target}{file} and patch its spec, then re-run validate_project.";
        }

        return null;
    }

    private static string PropertyFormHint(string? activityType, string property) {
        if (activityType is not null
            && ActivityCatalog.TryGet(activityType, out var schema)) {
            var match = schema.Properties.FirstOrDefault(p =>
                string.Equals(p.Name, property, StringComparison.OrdinalIgnoreCase));
            if (match is not null) {
                return match.Kind switch {
                    PropertyKind.Expression => " using an [expression] or a quoted literal",
                    PropertyKind.Literal => " as a raw literal (no [brackets])",
                    PropertyKind.TypeArgument => " as a type name (no [brackets])",
                    _ => ""
                };
            }
        }

        return "";
    }

    private static string? ReadPropertyValue(XElement? element, string property) {
        if (element is null) {
            return null;
        }

        foreach (var attr in element.Attributes()) {
            var local = attr.Name.LocalName;
            if (string.Equals(local, property, StringComparison.OrdinalIgnoreCase)) {
                return attr.Value;
            }

            if (property.Equals("TypeArgument", StringComparison.OrdinalIgnoreCase)
                && local.Equals("TypeArguments", StringComparison.OrdinalIgnoreCase)) {
                return attr.Value;
            }
        }

        foreach (var child in element.Elements()) {
            var local = child.Name.LocalName;
            var suffix = local.Contains('.') ? local[(local.LastIndexOf('.') + 1)..] : local;
            if (string.Equals(suffix, property, StringComparison.OrdinalIgnoreCase)) {
                var text = child.Value.Trim();
                return text.Length > 0 ? text : null;
            }
        }

        return null;
    }
}
