using UiPath.Engineering.Mcp.Core.Authoring;

namespace UiPath.Engineering.Mcp.Providers.UiPathCli;

public sealed class CliActivityDiscovery : IActivityDiscovery {
    private static readonly char[] RejectedQueryChars = ['&', '|', '<', '>', '%', '^', '\r', '\n'];

    private readonly IUiPathCliProvider _cli;

    public CliActivityDiscovery(IUiPathCliProvider cli) => _cli = cli;

    public async Task<IReadOnlyList<DiscoveredActivity>> FindAsync(
        string projectPath, string query, CancellationToken cancellationToken = default) {
        var sanitized = Sanitize(query);
        if (string.IsNullOrWhiteSpace(sanitized)) {
            return [];
        }

        var arguments = $"rpa activities find --query \"{sanitized}\" --output json";
        var result = await _cli.RunAsync("rpa", arguments, projectPath, cancellationToken);
        if (!result.Success) {
            throw new InvalidOperationException(
                result.Summary.Length > 0 ? result.Summary : "Activity discovery CLI call failed.");
        }

        var json = string.IsNullOrWhiteSpace(result.StdOut) ? result.Summary : result.StdOut;
        var parsed = ActivityFindParser.Parse(json);
        if (parsed.Count > 0) {
            return parsed;
        }

        if (result.RawOutputLines.Count > 0) {
            return ActivityFindParser.Parse(string.Join("\n", result.RawOutputLines));
        }

        return [];
    }

    internal static string Sanitize(string query) {
        var chars = query.Trim().ToCharArray();
        for (var i = 0; i < chars.Length; i++) {
            if (RejectedQueryChars.Contains(chars[i]) || chars[i] == '"') {
                chars[i] = ' ';
            }
        }

        var collapsed = System.Text.RegularExpressions.Regex.Replace(string.Concat(chars), @"\s+", " ").Trim();
        return collapsed;
    }

    // Starter XAML for one activity class, used to give a discovered activity a
    // real property surface. Only a single class name is interpolated, sanitized
    // the same way a query is — the CLI is never handed free-form user input.
    public async Task<string?> GetDefaultXamlAsync(
        string projectPath, string activityClassName, CancellationToken cancellationToken = default) {
        var sanitized = Sanitize(activityClassName);
        if (string.IsNullOrWhiteSpace(sanitized)) {
            return null;
        }

        var arguments = $"rpa activities get-default-xaml --activity-class-name \"{sanitized}\" --output json";
        var result = await _cli.RunAsync("rpa", arguments, projectPath, cancellationToken);
        if (!result.Success && string.IsNullOrWhiteSpace(result.StdOut)) {
            return null;
        }

        var payload = !string.IsNullOrWhiteSpace(result.StdOut) ? result.StdOut : result.Summary;
        return DefaultXamlParser.ExtractXaml(payload);
    }
}
