using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.IdentityProvider.Configuration;
using MintPlayer.Spark.IdentityProvider.Indexes;
using MintPlayer.Spark.IdentityProvider.Models;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.IdentityProvider.Endpoints;

internal static class Authorize
{
    public static async Task Handle(HttpContext context)
    {
        var ct = context.RequestAborted;
        var query = context.Request.Query;

        var clientId = query["client_id"].FirstOrDefault();
        var redirectUri = query["redirect_uri"].FirstOrDefault();
        var responseType = query["response_type"].FirstOrDefault();
        var scope = query["scope"].FirstOrDefault();
        var state = query["state"].FirstOrDefault();
        var codeChallenge = query["code_challenge"].FirstOrDefault();
        var codeChallengeMethod = query["code_challenge_method"].FirstOrDefault();
        var nonce = query["nonce"].FirstOrDefault();

        // Validate required parameters
        if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(redirectUri) ||
            string.IsNullOrEmpty(responseType) || string.IsNullOrEmpty(scope))
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsJsonAsync(new { error = "invalid_request", error_description = "Missing required parameters." });
            return;
        }

        if (responseType != "code")
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsJsonAsync(new { error = "unsupported_response_type", error_description = "Only 'code' response type is supported." });
            return;
        }

        // Lookup client application
        var store = context.RequestServices.GetRequiredService<IDocumentStore>();
        using var session = store.OpenAsyncSession();

        var app = await FindApplicationByClientIdAsync(session, clientId, ct);
        if (app == null || !app.Enabled)
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsJsonAsync(new { error = "invalid_client", error_description = "Unknown or disabled client." });
            return;
        }

        // Validate redirect URI
        if (!app.RedirectUris.Contains(redirectUri, StringComparer.Ordinal))
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsJsonAsync(new { error = "invalid_request", error_description = "Invalid redirect_uri." });
            return;
        }

        // Validate PKCE
        if (app.RequirePkce && string.IsNullOrEmpty(codeChallenge))
        {
            RedirectWithError(context, redirectUri, state, "invalid_request", "PKCE code_challenge is required.");
            return;
        }

        if (!string.IsNullOrEmpty(codeChallenge) && codeChallengeMethod != "S256")
        {
            RedirectWithError(context, redirectUri, state, "invalid_request", "Only S256 code_challenge_method is supported.");
            return;
        }

        // Validate requested scopes
        var requestedScopes = scope.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        foreach (var s in requestedScopes)
        {
            if (!app.AllowedScopes.Contains(s, StringComparer.OrdinalIgnoreCase))
            {
                RedirectWithError(context, redirectUri, state, "invalid_scope", $"Scope '{s}' is not allowed for this client.");
                return;
            }
        }

        // Check user authentication
        var userId = context.User?.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
        {
            // User not authenticated — redirect to MVC login page
            var currentUrl = context.Request.QueryString.Value;
            var loginUrl = $"/connect/login?returnUrl={Uri.EscapeDataString($"/connect/authorize{currentUrl}")}";
            context.Response.Redirect(loginUrl);
            return;
        }

        // Check existing consent
        var options = context.RequestServices.GetRequiredService<SparkIdentityProviderOptions>();

        if (app.ConsentType == "implicit" && options.AutoApproveImplicitConsent)
        {
            // Auto-approve — generate code and redirect
            await GenerateCodeAndRedirectAsync(context, session, app, userId, requestedScopes,
                redirectUri, state, codeChallenge, codeChallengeMethod, nonce, ct);
            return;
        }

        // Check if user already consented for these scopes
        var existingAuth = await session
            .Query<OidcAuthorization, OidcAuthorizations_BySubjectAndApplication>()
            .Where(a => a.Subject == userId && a.ApplicationId == app.Id! && a.Status == "valid")
            .FirstOrDefaultAsync(ct);

        if (existingAuth != null)
        {
            var allScopesCovered = requestedScopes.All(s =>
                existingAuth.GrantedScopes.Contains(s, StringComparer.OrdinalIgnoreCase));

            if (allScopesCovered)
            {
                await GenerateCodeAndRedirectAsync(context, session, app, userId, requestedScopes,
                    redirectUri, state, codeChallenge, codeChallengeMethod, nonce, ct);
                return;
            }
        }

        // Redirect to consent page
        var consentUrl = $"/connect/consent?client_id={Uri.EscapeDataString(clientId)}" +
            $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
            $"&scope={Uri.EscapeDataString(scope)}" +
            $"&state={Uri.EscapeDataString(state ?? "")}" +
            $"&code_challenge={Uri.EscapeDataString(codeChallenge ?? "")}" +
            $"&code_challenge_method={Uri.EscapeDataString(codeChallengeMethod ?? "")}" +
            $"&nonce={Uri.EscapeDataString(nonce ?? "")}" +
            $"&response_type=code";
        context.Response.Redirect(consentUrl);
    }

    internal static async Task GenerateCodeAndRedirectAsync(
        HttpContext context,
        IAsyncDocumentSession session,
        OidcApplication app,
        string userId,
        List<string> scopes,
        string redirectUri,
        string? state,
        string? codeChallenge,
        string? codeChallengeMethod,
        string? nonce,
        CancellationToken ct)
    {
        // Generate opaque authorization code
        var code = GenerateAuthorizationCode();

        // Create OidcToken document for the authorization code
        var token = new OidcToken
        {
            ApplicationId = (string)app.Id!,
            AuthorizationId = "", // Will be linked when consent is created
            Subject = userId,
            Type = "authorization_code",
            ReferenceId = code,
            CodeChallenge = codeChallenge,
            CodeChallengeMethod = codeChallengeMethod,
            RedirectUri = redirectUri,
            Scopes = scopes,
            Status = "valid",
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddMinutes(5), // 5 minute lifetime
            State = nonce, // Store nonce for ID token generation
        };

        await session.StoreAsync(token, ct);
        await session.SaveChangesAsync(ct);

        // Redirect back to client with authorization code
        var redirectUrl = $"{redirectUri}?code={Uri.EscapeDataString(code)}";
        if (!string.IsNullOrEmpty(state))
        {
            redirectUrl += $"&state={Uri.EscapeDataString(state)}";
        }

        context.Response.Redirect(redirectUrl);
    }

    private static string GenerateAuthorizationCode()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    internal static async Task<OidcApplication?> FindApplicationByClientIdAsync(
        IAsyncDocumentSession session, string clientId, CancellationToken ct)
    {
        return await session.Query<OidcApplication, OidcApplications_ByClientId>()
            .Where(a => a.ClientId == clientId)
            .FirstOrDefaultAsync(ct);
    }

    private static void RedirectWithError(HttpContext context, string redirectUri, string? state,
        string error, string description)
    {
        var url = $"{redirectUri}?error={Uri.EscapeDataString(error)}&error_description={Uri.EscapeDataString(description)}";
        if (!string.IsNullOrEmpty(state))
        {
            url += $"&state={Uri.EscapeDataString(state)}";
        }
        context.Response.Redirect(url);
    }
}
