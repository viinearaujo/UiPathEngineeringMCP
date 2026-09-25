namespace UiPath.Engineering.Mcp.Core.Configuration;

/// <summary>
/// Fixed-window rate limit for HTTP <c>/sse</c> only (<c>/health</c> is excluded).
/// Disabled by default so local and automated tests stay stable.
/// </summary>
public sealed class RateLimitOptions {
    public bool Enabled { get; set; }

    /// <summary>Max requests per window per remote IP. Default 60.</summary>
    public int PermitLimit { get; set; } = 60;

    /// <summary>Window length in seconds. Default 60 (one minute).</summary>
    public int WindowSeconds { get; set; } = 60;
}
