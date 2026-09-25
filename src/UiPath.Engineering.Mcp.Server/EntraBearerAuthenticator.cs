using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using UiPath.Engineering.Mcp.Core.Configuration;

namespace UiPath.Engineering.Mcp.Server;

/// <summary>
/// Validates an Entra ID Bearer JWT via the registered JwtBearer scheme.
/// Failures return false so the caller can fall through to API-key comparison.
/// Never logs tokens or Authorization header values.
/// </summary>
public interface IEntraBearerAuthenticator {
    Task<bool> TryAuthenticateAsync(HttpContext context, EntraAuthOptions options, CancellationToken cancellationToken = default);
}

public sealed class EntraBearerAuthenticator : IEntraBearerAuthenticator {
    public const string AuthenticationScheme = "EntraBearer";
    public const string ObjectIdClaimType = "oid";
    public const string ObjectIdClaimTypeLong = "http://schemas.microsoft.com/identity/claims/objectidentifier";

    public async Task<bool> TryAuthenticateAsync(
        HttpContext context,
        EntraAuthOptions options,
        CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(options);

        if (!options.Enabled) {
            return false;
        }

        AuthenticateResult result;
        try {
            result = await context.AuthenticateAsync(AuthenticationScheme);
        } catch {
            return false;
        }

        if (!result.Succeeded || result.Principal?.Identity?.IsAuthenticated != true) {
            return false;
        }

        return IsObjectIdAllowed(result.Principal, options.AllowedObjectIds);
    }

    internal static bool IsObjectIdAllowed(ClaimsPrincipal principal, IReadOnlyList<string>? allowedObjectIds) {
        if (allowedObjectIds is null || allowedObjectIds.Count == 0) {
            return true;
        }

        var oid = principal.FindFirst(ObjectIdClaimType)?.Value
            ?? principal.FindFirst(ObjectIdClaimTypeLong)?.Value;
        if (string.IsNullOrEmpty(oid)) {
            return false;
        }

        for (var i = 0; i < allowedObjectIds.Count; i++) {
            if (string.Equals(allowedObjectIds[i], oid, StringComparison.OrdinalIgnoreCase)) {
                return true;
            }
        }

        return false;
    }
}

public static class EntraJwtServiceCollectionExtensions {
    /// <summary>
    /// Registers JwtBearer validation pinned to <paramref name="entra"/> tenant and audience
    /// when Entra is enabled with non-empty TenantId and Audience. No-op otherwise.
    /// </summary>
    public static IServiceCollection AddMcpEntraJwtAuth(this IServiceCollection services, EntraAuthOptions? entra) {
        services.AddSingleton<IEntraBearerAuthenticator, EntraBearerAuthenticator>();

        if (entra is null
            || !entra.Enabled
            || string.IsNullOrWhiteSpace(entra.TenantId)
            || string.IsNullOrWhiteSpace(entra.Audience)) {
            return services;
        }

        var tenantId = entra.TenantId.Trim();
        var audience = entra.Audience.Trim();
        var authority = $"https://login.microsoftonline.com/{tenantId}/v2.0";

        services.AddAuthentication()
            .AddJwtBearer(EntraBearerAuthenticator.AuthenticationScheme, options => {
                options.Authority = authority;
                options.Audience = audience;
                options.MapInboundClaims = false;
                options.TokenValidationParameters.ValidateAudience = true;
                options.TokenValidationParameters.ValidAudience = audience;
                options.TokenValidationParameters.ValidateIssuer = true;
                options.TokenValidationParameters.ValidIssuers = [
                    $"https://login.microsoftonline.com/{tenantId}/v2.0",
                    $"https://sts.windows.net/{tenantId}/"
                ];
                options.TokenValidationParameters.ValidateLifetime = true;
                options.TokenValidationParameters.NameClaimType = "name";
                options.Events = new JwtBearerEvents {
                    // Keep failures silent so middleware can fall through to API-key compare.
                    OnAuthenticationFailed = _ => Task.CompletedTask,
                    OnChallenge = context => {
                        context.HandleResponse();
                        return Task.CompletedTask;
                    }
                };
            });

        return services;
    }

    public static EntraAuthOptions? ReadEntraAuthOptions(IConfiguration configuration) =>
        configuration.GetSection("McpServer:HttpAuth:Entra").Get<EntraAuthOptions>();

    public static RateLimitOptions ReadRateLimitOptions(IConfiguration configuration) =>
        configuration.GetSection("McpServer:RateLimit").Get<RateLimitOptions>() ?? new RateLimitOptions();
}
