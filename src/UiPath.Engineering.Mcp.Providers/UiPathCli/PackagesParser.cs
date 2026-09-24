using System.Text.Json;

namespace UiPath.Engineering.Mcp.Providers.UiPathCli;

/// <summary>Available versions of one NuGet package, as reported by <c>packages versions</c>.</summary>
public sealed record PackageVersions {
    public string PackageId { get; init; } = string.Empty;
    public bool IncludePrerelease { get; init; }
    public List<string> Versions { get; init; } = [];

    /// <summary>Newest version in the list (the CLI sorts descending).</summary>
    public string? Latest => Versions.Count > 0 ? Versions[0] : null;

    /// <summary>Newest non-prerelease version.</summary>
    public string? LatestStable =>
        Versions.FirstOrDefault(v => !v.Contains('-', StringComparison.Ordinal));
}

/// <summary>Outcome of one <c>packages install</c> call.</summary>
public sealed record PackageInstallOutcome {
    public List<string> Requested { get; init; } = [];
    public List<string> Failed { get; init; } = [];
    public string? Message { get; init; }
    public bool Succeeded => Failed.Count == 0;
}

/// <summary>
/// Parses the <c>uip rpa packages versions|install|inspect</c> payloads. Versions arrive as
/// <c>Data:{packageId, includePrerelease, versions:[...]}</c>; install reports the packages that
/// failed. Property casing differs between CLI versions, so lookups are case-insensitive.
/// </summary>
public static class PackagesParser {
    public static PackageVersions ParseVersions(JsonElement? data) {
        if (data is not { } element || element.ValueKind != JsonValueKind.Object) {
            return new PackageVersions();
        }

        var versions = new List<string>();
        if (CliEnvelopeParser.TryGetProperty(element, "versions", out var array)
            && array.ValueKind == JsonValueKind.Array) {
            foreach (var item in array.EnumerateArray()) {
                var text = item.ValueKind switch {
                    JsonValueKind.String => item.GetString(),
                    JsonValueKind.Object => CliEnvelopeParser.GetString(item, "version", "Version"),
                    _ => null
                };

                if (!string.IsNullOrWhiteSpace(text)) {
                    versions.Add(text!);
                }
            }
        }

        return new PackageVersions {
            PackageId = CliEnvelopeParser.GetString(element, "packageId", "PackageId", "id", "Id") ?? string.Empty,
            IncludePrerelease = CliEnvelopeParser.GetBool(element, "includePrerelease", "IncludePrerelease") ?? false,
            Versions = versions
        };
    }

    public static PackageInstallOutcome ParseInstall(JsonElement? data, IReadOnlyList<string> requested, string? envelopeMessage) {
        var failed = new List<string>();

        if (data is { } element) {
            foreach (var name in new[] { "failedPackages", "failed", "failures", "errors" }) {
                if (!CliEnvelopeParser.TryGetProperty(element, name, out var array)
                    || array.ValueKind != JsonValueKind.Array) {
                    continue;
                }

                foreach (var item in array.EnumerateArray()) {
                    var text = item.ValueKind switch {
                        JsonValueKind.String => item.GetString(),
                        JsonValueKind.Object => FormatFailure(item),
                        _ => null
                    };

                    if (!string.IsNullOrWhiteSpace(text)) {
                        failed.Add(text!);
                    }
                }
            }
        }

        return new PackageInstallOutcome {
            Requested = [.. requested],
            Failed = failed,
            Message = envelopeMessage
        };
    }

    private static string FormatFailure(JsonElement item) {
        var id = CliEnvelopeParser.GetString(item, "id", "Id", "packageId", "PackageId", "name", "Name");
        var version = CliEnvelopeParser.GetString(item, "version", "Version");
        var reason = CliEnvelopeParser.GetString(item, "message", "Message", "error", "Error", "reason", "Reason");
        var label = id ?? "package";
        if (!string.IsNullOrWhiteSpace(version)) {
            label += $"@{version}";
        }

        return string.IsNullOrWhiteSpace(reason) ? label : $"{label}: {reason}";
    }

    /// <summary>
    /// Renders a package spec as the CLI's comma-joined <c>key=value</c> form
    /// (<c>id=X,version=Y</c>). One <c>--packages</c> occurrence per package.
    /// </summary>
    public static string FormatPackageSpec(string packageId, string? version) {
        var spec = $"id={packageId}";
        return string.IsNullOrWhiteSpace(version) ? spec : $"{spec},version={version}";
    }

    /// <summary>
    /// Reads the markdown documentation that <c>packages inspect</c> returns. The payload is
    /// either the markdown string itself or an object carrying it on a message/markdown field.
    /// </summary>
    public static string? ReadMarkdown(JsonElement? data) {
        if (data is not { } element) {
            return null;
        }

        if (element.ValueKind == JsonValueKind.String) {
            var text = element.GetString();
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }

        if (element.ValueKind != JsonValueKind.Object) {
            return null;
        }

        return CliEnvelopeParser.GetString(
            element,
            "markdown", "Markdown",
            "documentation", "Documentation",
            "apiDocumentation", "ApiDocumentation",
            "message", "Message",
            "content", "Content",
            "text", "Text");
    }
}
