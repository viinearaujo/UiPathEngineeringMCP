namespace UiPath.Engineering.Mcp.Core.Configuration;

/// <summary>
/// Optional Entra ID (Azure AD) JWT acceptance on HTTP <c>/sse</c>.
/// Disabled by default. When enabled, a valid Bearer JWT for the configured
/// tenant/audience is accepted alongside the API key. An empty
/// <see cref="AllowedObjectIds"/> list means any authenticated principal is allowed;
/// a non-empty list requires the token <c>oid</c> claim to match.
/// </summary>
public sealed class EntraAuthOptions {
    public bool Enabled { get; set; }

    /// <summary>Directory (tenant) ID. Tokens are pinned to this tenant's issuer.</summary>
    public string TenantId { get; set; } = string.Empty;

    /// <summary>API app registration audience (app ID URI or client ID).</summary>
    public string Audience { get; set; } = string.Empty;

    /// <summary>
    /// When non-empty, the JWT <c>oid</c> claim must be one of these object IDs.
    /// </summary>
    public List<string> AllowedObjectIds { get; set; } = [];
}
