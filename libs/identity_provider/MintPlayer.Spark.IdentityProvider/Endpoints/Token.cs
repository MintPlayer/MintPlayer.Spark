using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.IdentityProvider.Indexes;
using MintPlayer.Spark.IdentityProvider.Models;
using MintPlayer.Spark.IdentityProvider.Services;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.IdentityProvider.Endpoints;

internal static class Token
{
    public static async Task Handle(HttpContext context)
    {
        var ct = context.RequestAborted;

        if (!context.Request.HasFormContentType)
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsJsonAsync(new { error = "invalid_request", error_description = "Content-Type must be application/x-www-form-urlencoded." });
            return;
        }

        var form = await context.Request.ReadFormAsync(ct);
        var grantType = form["grant_type"].FirstOrDefault();

        switch (grantType)
        {
            case "authorization_code":
                await HandleAuthorizationCodeGrant(context, form, ct);
                break;
            case "refresh_token":
                await HandleRefreshTokenGrant(context, form, ct);
                break;
            case "client_credentials":
                await HandleClientCredentialsGrant(context, form, ct);
                break;
            default:
                context.Response.StatusCode = 400;
                await context.Response.WriteAsJsonAsync(new { error = "unsupported_grant_type" });
                break;
        }
    }

    private static async Task HandleAuthorizationCodeGrant(HttpContext context, IFormCollection form, CancellationToken ct)
    {
        var clientId = form["client_id"].FirstOrDefault();
        var clientSecret = form["client_secret"].FirstOrDefault();
        var code = form["code"].FirstOrDefault();
        var redirectUri = form["redirect_uri"].FirstOrDefault();
        var codeVerifier = form["code_verifier"].FirstOrDefault();

        if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(code) || string.IsNullOrEmpty(redirectUri))
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsJsonAsync(new { error = "invalid_request", error_description = "Missing required parameters." });
            return;
        }

        var store = context.RequestServices.GetRequiredService<IDocumentStore>();
        using var session = store.OpenAsyncSession();

        // Validate client
        var app = await Authorize.FindApplicationByClientIdAsync(session, clientId, ct);
        if (app == null || !app.Enabled)
        {
            context.Response.StatusCode = 401;
            await context.Response.WriteAsJsonAsync(new { error = "invalid_client" });
            return;
        }

        // Check grant type is allowed
        if (!app.AllowedGrantTypes.Contains("authorization_code", StringComparer.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsJsonAsync(new { error = "unauthorized_client", error_description = "This client is not authorized for authorization_code grant." });
            return;
        }

        // Validate client secret for confidential clients
        if (app.ClientType == "confidential")
        {
            if (string.IsNullOrEmpty(clientSecret) || !VerifyClientSecret(clientSecret, app.Secrets))
            {
                context.Response.StatusCode = 401;
                await context.Response.WriteAsJsonAsync(new { error = "invalid_client", error_description = "Invalid client credentials." });
                return;
            }
        }

        // Find the authorization code token
        // Point-load, not an index query: index results are eventually consistent, so a code
        // redeemed moments ago could still read back as "valid" and be replayed. Status is
        // therefore checked on the loaded document rather than in the lookup predicate.
        var codeToken = await session.LoadAsync<OidcToken>(OidcTokenReference.DocumentId(code), ct);
        if (codeToken is { Type: "authorization_code", Status: not "valid" })
        {
            // A code presented twice: the first redemption already consumed it. Everything
            // derived from it is now suspect, so the whole authorization is torn down.
            await RevokeAuthorizationChainAsync(session, codeToken, ct);
            await session.SaveChangesAsync(ct);

            context.Response.StatusCode = 400;
            await context.Response.WriteAsJsonAsync(new { error = "invalid_grant", error_description = "Invalid or expired authorization code." });
            return;
        }

        if (codeToken is not { Type: "authorization_code" })
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsJsonAsync(new { error = "invalid_grant", error_description = "Invalid or expired authorization code." });
            return;
        }

        // Validate the code hasn't expired
        if (codeToken.ExpiresAt < DateTime.UtcNow)
        {
            codeToken.Status = "expired";
            await session.SaveChangesAsync(ct);
            context.Response.StatusCode = 400;
            await context.Response.WriteAsJsonAsync(new { error = "invalid_grant", error_description = "Authorization code has expired." });
            return;
        }

        // Validate redirect_uri matches
        if (!string.Equals(codeToken.RedirectUri, redirectUri, StringComparison.Ordinal))
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsJsonAsync(new { error = "invalid_grant", error_description = "redirect_uri mismatch." });
            return;
        }

        // Validate PKCE code_verifier
        if (!string.IsNullOrEmpty(codeToken.CodeChallenge))
        {
            if (string.IsNullOrEmpty(codeVerifier))
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsJsonAsync(new { error = "invalid_grant", error_description = "PKCE code_verifier is required." });
                return;
            }

            var computedChallenge = ComputeS256Challenge(codeVerifier);
            if (!string.Equals(computedChallenge, codeToken.CodeChallenge, StringComparison.Ordinal))
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsJsonAsync(new { error = "invalid_grant", error_description = "PKCE verification failed." });
                return;
            }
        }

        // Mark code as redeemed (single-use)
        codeToken.Status = "redeemed";
        codeToken.RedeemedAt = DateTime.UtcNow;

        // Load user
        var user = await LoadUserAsync(context.RequestServices, codeToken.Subject, ct);
        if (user == null)
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsJsonAsync(new { error = "invalid_grant", error_description = "User not found." });
            return;
        }

        // Load scope definitions from DB
        var grantedScopes = await LoadScopesAsync(session, codeToken.Scopes, ct);

        var issuer = $"{context.Request.Scheme}://{context.Request.Host}";
        var tokenGenerator = context.RequestServices.GetRequiredService<OidcTokenGenerator>();

        // Generate tokens
        var accessToken = tokenGenerator.GenerateAccessToken(user, app, issuer, grantedScopes, app.AccessTokenLifetimeMinutes);
        var idToken = tokenGenerator.GenerateIdToken(user, app, issuer, grantedScopes, codeToken.State, app.AccessTokenLifetimeMinutes);
        var refreshTokenValue = tokenGenerator.GenerateRefreshToken();

        // Store access token
        var accessTokenDoc = new OidcToken
        {
            ApplicationId = app.Id!,
            AuthorizationId = codeToken.AuthorizationId,
            Subject = codeToken.Subject,
            Type = "access_token",
            Payload = accessToken,
            Scopes = codeToken.Scopes,
            Status = "valid",
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddMinutes(app.AccessTokenLifetimeMinutes),
        };

        // Store refresh token
        var refreshTokenDoc = new OidcToken
        {
            ApplicationId = app.Id!,
            AuthorizationId = codeToken.AuthorizationId,
            Subject = codeToken.Subject,
            Id = OidcTokenReference.DocumentId(refreshTokenValue),
            Type = "refresh_token",
            Scopes = codeToken.Scopes,
            Status = "valid",
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(app.RefreshTokenLifetimeDays),
        };

        await session.StoreAsync(accessTokenDoc, ct);
        await session.StoreAsync(refreshTokenDoc, ct);
        await session.SaveChangesAsync(ct);

        context.Response.ContentType = "application/json";
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Pragma = "no-cache";

        await context.Response.WriteAsJsonAsync(new
        {
            access_token = accessToken,
            token_type = "Bearer",
            expires_in = app.AccessTokenLifetimeMinutes * 60,
            id_token = idToken,
            refresh_token = refreshTokenValue,
        });
    }

    private static async Task HandleRefreshTokenGrant(HttpContext context, IFormCollection form, CancellationToken ct)
    {
        var clientId = form["client_id"].FirstOrDefault();
        var clientSecret = form["client_secret"].FirstOrDefault();
        var refreshToken = form["refresh_token"].FirstOrDefault();

        if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(refreshToken))
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsJsonAsync(new { error = "invalid_request" });
            return;
        }

        var store = context.RequestServices.GetRequiredService<IDocumentStore>();
        using var session = store.OpenAsyncSession();

        var app = await Authorize.FindApplicationByClientIdAsync(session, clientId, ct);
        if (app == null || !app.Enabled)
        {
            context.Response.StatusCode = 401;
            await context.Response.WriteAsJsonAsync(new { error = "invalid_client" });
            return;
        }

        if (app.ClientType == "confidential")
        {
            if (string.IsNullOrEmpty(clientSecret) || !VerifyClientSecret(clientSecret, app.Secrets))
            {
                context.Response.StatusCode = 401;
                await context.Response.WriteAsJsonAsync(new { error = "invalid_client" });
                return;
            }
        }

        // Find refresh token
        // Point-load for the same reason as the authorization-code path above.
        var refreshTokenDoc = await session.LoadAsync<OidcToken>(OidcTokenReference.DocumentId(refreshToken), ct);
        if (refreshTokenDoc is { Type: "refresh_token", Status: not "valid" })
        {
            // Reuse of an already-rotated refresh token. Per RFC 6819 §5.2.2.3 this is
            // treated as theft: revoke the entire chain rather than just refusing.
            await RevokeAuthorizationChainAsync(session, refreshTokenDoc, ct);
            await session.SaveChangesAsync(ct);

            context.Response.StatusCode = 400;
            await context.Response.WriteAsJsonAsync(new { error = "invalid_grant", error_description = "Invalid or expired refresh token." });
            return;
        }

        if (refreshTokenDoc is not { Type: "refresh_token", Status: "valid" })
            refreshTokenDoc = null;

        if (refreshTokenDoc == null || refreshTokenDoc.ExpiresAt < DateTime.UtcNow)
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsJsonAsync(new { error = "invalid_grant", error_description = "Invalid or expired refresh token." });
            return;
        }

        // Load user
        var user = await LoadUserAsync(context.RequestServices, refreshTokenDoc.Subject, ct);
        if (user == null)
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsJsonAsync(new { error = "invalid_grant" });
            return;
        }

        // Load scope definitions from DB
        var grantedScopes = await LoadScopesAsync(session, refreshTokenDoc.Scopes, ct);

        var issuer = $"{context.Request.Scheme}://{context.Request.Host}";
        var tokenGenerator = context.RequestServices.GetRequiredService<OidcTokenGenerator>();

        // Generate new tokens
        var newAccessToken = tokenGenerator.GenerateAccessToken(user, app, issuer, grantedScopes, app.AccessTokenLifetimeMinutes);
        var newIdToken = tokenGenerator.GenerateIdToken(user, app, issuer, grantedScopes, null, app.AccessTokenLifetimeMinutes);
        var newRefreshTokenValue = tokenGenerator.GenerateRefreshToken();

        // Revoke old refresh token
        refreshTokenDoc.Status = "redeemed";
        refreshTokenDoc.RedeemedAt = DateTime.UtcNow;

        // Store new tokens
        var newAccessTokenDoc = new OidcToken
        {
            ApplicationId = app.Id!,
            AuthorizationId = refreshTokenDoc.AuthorizationId,
            Subject = refreshTokenDoc.Subject,
            Type = "access_token",
            Payload = newAccessToken,
            Scopes = refreshTokenDoc.Scopes,
            Status = "valid",
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddMinutes(app.AccessTokenLifetimeMinutes),
        };

        var newRefreshTokenDoc = new OidcToken
        {
            ApplicationId = app.Id!,
            AuthorizationId = refreshTokenDoc.AuthorizationId,
            Subject = refreshTokenDoc.Subject,
            Id = OidcTokenReference.DocumentId(newRefreshTokenValue),
            Type = "refresh_token",
            Scopes = refreshTokenDoc.Scopes,
            Status = "valid",
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(app.RefreshTokenLifetimeDays),
        };

        await session.StoreAsync(newAccessTokenDoc, ct);
        await session.StoreAsync(newRefreshTokenDoc, ct);
        await session.SaveChangesAsync(ct);

        context.Response.ContentType = "application/json";
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Pragma = "no-cache";

        await context.Response.WriteAsJsonAsync(new
        {
            access_token = newAccessToken,
            token_type = "Bearer",
            expires_in = app.AccessTokenLifetimeMinutes * 60,
            id_token = newIdToken,
            refresh_token = newRefreshTokenValue,
        });
    }

    private static async Task HandleClientCredentialsGrant(HttpContext context, IFormCollection form, CancellationToken ct)
    {
        var clientId = form["client_id"].FirstOrDefault();
        var clientSecret = form["client_secret"].FirstOrDefault();
        var scope = form["scope"].FirstOrDefault();

        if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(clientSecret))
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsJsonAsync(new { error = "invalid_request", error_description = "client_id and client_secret are required." });
            return;
        }

        var store = context.RequestServices.GetRequiredService<IDocumentStore>();
        using var session = store.OpenAsyncSession();

        var app = await Authorize.FindApplicationByClientIdAsync(session, clientId, ct);
        if (app == null || !app.Enabled)
        {
            context.Response.StatusCode = 401;
            await context.Response.WriteAsJsonAsync(new { error = "invalid_client" });
            return;
        }

        // Check grant type is allowed
        if (!app.AllowedGrantTypes.Contains("client_credentials", StringComparer.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsJsonAsync(new { error = "unauthorized_client", error_description = "This client is not authorized for client_credentials grant." });
            return;
        }

        // Validate client secret
        if (!VerifyClientSecret(clientSecret, app.Secrets))
        {
            context.Response.StatusCode = 401;
            await context.Response.WriteAsJsonAsync(new { error = "invalid_client", error_description = "Invalid client credentials." });
            return;
        }

        // Parse and validate requested scopes
        var requestedScopes = (scope ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        foreach (var s in requestedScopes)
        {
            if (!app.AllowedScopes.Contains(s, StringComparer.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsJsonAsync(new { error = "invalid_scope", error_description = $"Scope '{s}' is not allowed for this client." });
                return;
            }
        }

        // If no scopes requested, use all allowed scopes
        if (requestedScopes.Count == 0)
        {
            requestedScopes = app.AllowedScopes.ToList();
        }

        // Load scope definitions from DB
        var grantedScopes = await LoadScopesAsync(session, requestedScopes, ct);

        var issuer = $"{context.Request.Scheme}://{context.Request.Host}";
        var tokenGenerator = context.RequestServices.GetRequiredService<OidcTokenGenerator>();

        // Generate access token only (no user, no ID token, no refresh token)
        var accessToken = tokenGenerator.GenerateAccessToken(null, app, issuer, grantedScopes, app.AccessTokenLifetimeMinutes);

        // Store access token
        var accessTokenDoc = new OidcToken
        {
            ApplicationId = app.Id!,
            Subject = $"client:{app.ClientId}",
            Type = "access_token",
            Payload = accessToken,
            Scopes = requestedScopes,
            Status = "valid",
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddMinutes(app.AccessTokenLifetimeMinutes),
        };

        await session.StoreAsync(accessTokenDoc, ct);
        await session.SaveChangesAsync(ct);

        context.Response.ContentType = "application/json";
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Pragma = "no-cache";

        await context.Response.WriteAsJsonAsync(new
        {
            access_token = accessToken,
            token_type = "Bearer",
            expires_in = app.AccessTokenLifetimeMinutes * 60,
        });
    }

    internal static async Task<SparkUser?> LoadUserAsync(IServiceProvider serviceProvider, string userId, CancellationToken ct)
    {
        var registry = serviceProvider.GetRequiredService<SparkModuleRegistry>();
        var userType = registry.IdentityUserType ?? typeof(SparkUser);

        var userManagerType = typeof(UserManager<>).MakeGenericType(userType);
        var userManager = serviceProvider.GetRequiredService(userManagerType);

        var findByIdMethod = userManagerType.GetMethod("FindByIdAsync")!;
        var result = await (dynamic)findByIdMethod.Invoke(userManager, [userId])!;
        return result as SparkUser;
    }

    internal static async Task<List<OidcScope>> LoadScopesAsync(IAsyncDocumentSession session, List<string> scopeNames, CancellationToken ct)
    {
        return await session
            .Query<OidcScope>()
            .Where(s => s.Name.In(scopeNames) && s.Enabled)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Revokes every token issued under the same authorization as <paramref name="compromised"/>.
    /// <para>
    /// Presenting a consumed authorization code, or a refresh token that has already been
    /// rotated away, is not a benign retry — the legitimate client holds the successor, so a
    /// replay means the old value leaked. RFC 6819 §5.2.2.3 calls for revoking the whole
    /// chain rather than merely refusing the request, which would leave the attacker's other
    /// stolen tokens working.
    /// </para>
    /// <para>
    /// The sweep is by <c>AuthorizationId</c> and therefore rides an eventually-consistent
    /// index: a token issued moments before the replay may be missed. That is a deliberate
    /// asymmetry — <em>detection</em> is exact (a point-load by id), only the blast-radius
    /// cleanup is best-effort, and it errs toward revoking too little rather than failing
    /// closed on a legitimate request.
    /// </para>
    /// </summary>
    private static async Task RevokeAuthorizationChainAsync(
        IAsyncDocumentSession session, OidcToken compromised, CancellationToken ct)
    {
        compromised.Status = "revoked";

        if (string.IsNullOrEmpty(compromised.AuthorizationId))
            return;

        var siblings = await session
            .Query<OidcToken>()
            .Where(t => t.AuthorizationId == compromised.AuthorizationId && t.Status == "valid")
            .ToListAsync(ct);

        foreach (var sibling in siblings)
            sibling.Status = "revoked";
    }

    internal static bool VerifyClientSecret(string secret, List<ClientSecret> secrets)
    {
        if (secrets.Count == 0) return false;

        var now = DateTime.UtcNow;

        // Every unexpired secret is checked even once one matches: short-circuiting on the
        // first hit would leak, through timing, which of a rotating set was presented.
        var matched = false;
        foreach (var candidate in secrets)
        {
            if (candidate.ExpiresAt != null && candidate.ExpiresAt <= now)
                continue;

            if (ClientSecretHasher.Verify(secret, candidate.Hash))
                matched = true;
        }

        return matched;
    }

    private static string ComputeS256Challenge(string codeVerifier)
    {
        var bytes = Encoding.ASCII.GetBytes(codeVerifier);
        var hash = SHA256.HashData(bytes);
        return Convert.ToBase64String(hash).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
