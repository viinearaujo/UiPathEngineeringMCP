namespace UiPath.Engineering.Mcp.Core.Configuration;

/// <summary>
/// API-key gate in front of HTTP <c>/sse</c>. Disabled is allowed only in
/// Development. Non-Development HTTP requires Enabled and a non-empty key at
/// startup. <c>/health</c> stays anonymous. Stdio is unauthenticated.
/// </summary>
public sealed class HttpAuthOptions {
    public bool Enabled { get; set; }

    /// <summary>Shared secret. Never logged. Empty while Enabled means fail closed.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Prior shared secret kept valid during rotation. Empty means ignored.
    /// Compared with the same fixed-time equality as <see cref="ApiKey"/>.
    /// </summary>
    public string PreviousApiKey { get; set; } = string.Empty;

    /// <summary>Header that carries the key. Copilot Studio can send this as an API-key header.</summary>
    public string HeaderName { get; set; } = HttpAuthEvaluator.DefaultHeaderName;

    /// <summary>Optional Entra ID JWT acceptance (second credential on <c>/sse</c>).</summary>
    public EntraAuthOptions Entra { get; set; } = new();
}
