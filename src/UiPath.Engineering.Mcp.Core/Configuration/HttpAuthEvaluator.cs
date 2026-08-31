using System.Security.Cryptography;
using System.Text;

namespace UiPath.Engineering.Mcp.Core.Configuration;

public static class HttpAuthEvaluator {
    public const string DefaultHeaderName = "X-Api-Key";
    public const string BearerPrefix = "Bearer ";

    public static bool IsAnonymousPath(string path) {
        if (string.IsNullOrEmpty(path)) {
            return false;
        }

        return path.Equals("/health", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/health/", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsProtectedPath(string path) {
        if (string.IsNullOrEmpty(path)) {
            return false;
        }

        return path.Equals("/sse", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/sse/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns null when HTTP hosting may start for <paramref name="environmentName"/>.
    /// Non-Development requires <see cref="HttpAuthOptions.Enabled"/> and a non-empty key.
    /// Development is unrestricted at startup; request-time still fails closed when
    /// Enabled is true and the key is empty.
    /// </summary>
    public static string? ValidateHttpStartup(HttpAuthOptions options, string? environmentName) {
        ArgumentNullException.ThrowIfNull(options);

        if (IsDevelopmentEnvironment(environmentName)) {
            return null;
        }

        if (!options.Enabled) {
            return "HTTP hosting outside Development requires McpServer:HttpAuth:Enabled to be true.";
        }

        if (string.IsNullOrWhiteSpace(options.ApiKey)) {
            return "HTTP hosting outside Development requires a non-empty McpServer:HttpAuth:ApiKey.";
        }

        return null;
    }

    public static bool IsDevelopmentEnvironment(string? environmentName) =>
        string.Equals(environmentName, "Development", StringComparison.OrdinalIgnoreCase);

    public static bool IsAuthorized(HttpAuthOptions options, string? headerValue, string? authorization) {
        if (!options.Enabled) {
            return true;
        }

        if (string.IsNullOrEmpty(options.ApiKey)) {
            return false;
        }

        if (FixedEquals(options.ApiKey, headerValue)) {
            return true;
        }

        if (authorization is not null
            && authorization.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase)) {
            return FixedEquals(options.ApiKey, authorization[BearerPrefix.Length..].Trim());
        }

        return false;
    }

    public static string ResolveHeaderName(HttpAuthOptions options) =>
        string.IsNullOrWhiteSpace(options.HeaderName) ? DefaultHeaderName : options.HeaderName;

    private static bool FixedEquals(string expected, string? provided) {
        if (string.IsNullOrEmpty(provided)) {
            return false;
        }

        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var providedBytes = Encoding.UTF8.GetBytes(provided);
        if (expectedBytes.Length != providedBytes.Length) {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(expectedBytes, providedBytes);
    }
}
