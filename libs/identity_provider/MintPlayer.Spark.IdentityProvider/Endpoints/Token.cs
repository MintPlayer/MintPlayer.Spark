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
        var codeToken = await session
            .Query<OidcToken, OidcTokens_ByReferenceId>()
            .Where(t => t.ReferenceId == code && t.Type == "authorization_code" && t.Status == "valid")
            .FirstOrDefaultAsync(ct);

        if (codeToken == null)
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
            Type = "refresh_token",
            ReferenceId = refreshTokenValue,
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
        var refreshTokenDoc = await session
            .Query<OidcToken, OidcTokens_ByReferenceId>()
            .Where(t => t.ReferenceId == refreshToken && t.Type == "refresh_token" && t.Status == "valid")
            .FirstOrDefaultAsync(ct);

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
            Type = "refresh_token",
            ReferenceId = newRefreshTokenValue,
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

    internal static bool VerifyClientSecret(string secret, List<ClientSecret> secrets)
    {
        if (secrets.Count == 0) return false;

        var secretBytes = Encoding.UTF8.GetBytes(secret);
        var hashBytes = SHA256.HashData(secretBytes);
        var computed = Convert.ToBase64String(hashBytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        var now = DateTime.UtcNow;
        return secrets.Any(s =>
            string.Equals(computed, s.Hash, StringComparison.Ordinal) &&
            (s.ExpiresAt == null || s.ExpiresAt > now));
    }

    private static string ComputeS256Challenge(string codeVerifier)
    {
        var bytes = Encoding.ASCII.GetBytes(codeVerifier);
        var hash = SHA256.HashData(bytes);
        return Convert.ToBase64String(hash).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
