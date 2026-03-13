using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using MintPlayer.Spark.Authorization.Services;

namespace MintPlayer.Spark.Authorization.Endpoints;

/// <summary>
/// GET /spark/auth/external-login/{scheme}?returnUrl=...
/// Initiates OIDC login flow by redirecting the user to the external provider.
/// </summary>
internal static class ExternalLogin
{
    private const string CookieName = ".SparkAuth.OidcState";
    private const string ProtectorPurpose = "SparkAuth.OidcState.v1";

    public static async Task<IResult> Handle(
        HttpContext httpContext,
        string scheme,
        OidcClientService oidcClientService,
        OidcProviderRegistry registry,
        IDataProtectionProvider dataProtectionProvider)
    {
        var returnUrl = httpContext.Request.Query["returnUrl"].FirstOrDefault() ?? "/";

        // Verify the scheme is registered
        var provider = registry.GetByScheme(scheme);
        if (provider == null)
        {
            return Results.BadRequest(new { error = $"Unknown external login provider: {scheme}" });
        }

        // Generate PKCE
        var (codeVerifier, codeChallenge) = oidcClientService.GeneratePkce();

        // Generate random state
        var stateBytes = RandomNumberGenerator.GetBytes(32);
        var state = Convert.ToBase64String(stateBytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        // Build the redirect URI (always /spark/auth/oidc-callback on the current host)
        var request = httpContext.Request;
        var redirectUri = $"{request.Scheme}://{request.Host}/spark/auth/oidc-callback";

        // Build authorization URL
        var authorizationUrl = await oidcClientService.BuildAuthorizationUrlAsync(
            scheme, redirectUri, state, codeChallenge, httpContext.RequestAborted);

        if (authorizationUrl == null)
        {
            return Results.Problem("Failed to build authorization URL. Check provider configuration.");
        }

        // Store state in encrypted cookie
        var statePayload = JsonSerializer.Serialize(new OidcStateCookie
        {
            Scheme = scheme,
            CodeVerifier = codeVerifier,
            State = state,
            ReturnUrl = returnUrl,
        });

        var protector = dataProtectionProvider.CreateProtector(ProtectorPurpose);
        var encryptedState = protector.Protect(statePayload);

        httpContext.Response.Cookies.Append(CookieName, encryptedState, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Lax, // Lax needed for external redirect
            MaxAge = TimeSpan.FromMinutes(10),
            Path = "/spark/auth",
        });

        return Results.Redirect(authorizationUrl);
    }
}

/// <summary>
/// Data stored in the encrypted state cookie during the OIDC flow.
/// </summary>
internal class OidcStateCookie
{
    public string Scheme { get; set; } = "";
    public string CodeVerifier { get; set; } = "";
    public string State { get; set; } = "";
    public string ReturnUrl { get; set; } = "/";
}
