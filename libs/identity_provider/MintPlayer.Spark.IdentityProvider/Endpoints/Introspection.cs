using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.IdentityProvider.Indexes;
using MintPlayer.Spark.IdentityProvider.Models;
using MintPlayer.Spark.IdentityProvider.Services;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Raven.Client.Documents;

namespace MintPlayer.Spark.IdentityProvider.Endpoints;

/// <summary>
/// Token Introspection Endpoint (RFC 7662).
/// Allows resource servers to validate tokens and retrieve their claims.
/// </summary>
internal static class Introspection
{
    public static async Task Handle(HttpContext context)
    {
        var ct = context.RequestAborted;

        if (!context.Request.HasFormContentType)
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsJsonAsync(new { error = "invalid_request" });
            return;
        }

        var form = await context.Request.ReadFormAsync(ct);
        var token = form["token"].FirstOrDefault();
        var tokenTypeHint = form["token_type_hint"].FirstOrDefault();
        var clientId = form["client_id"].FirstOrDefault();
        var clientSecret = form["client_secret"].FirstOrDefault();

        if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(clientSecret))
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsJsonAsync(new { error = "invalid_request", error_description = "token, client_id, and client_secret are required." });
            return;
        }

        var store = context.RequestServices.GetRequiredService<IDocumentStore>();
        using var session = store.OpenAsyncSession();

        // Authenticate client
        var app = await Authorize.FindApplicationByClientIdAsync(session, clientId, ct);
        if (app == null || !app.Enabled || !Token.VerifyClientSecret(clientSecret, app.Secrets))
        {
            context.Response.StatusCode = 401;
            await context.Response.WriteAsJsonAsync(new { error = "invalid_client" });
            return;
        }

        // Try refresh token first (by ReferenceId)
        if (tokenTypeHint is null or "refresh_token")
        {
            var refreshDoc = await session
                .Query<OidcToken, OidcTokens_ByReferenceId>()
                .Where(t => t.ReferenceId == token && t.Type == "refresh_token")
                .FirstOrDefaultAsync(ct);

            if (refreshDoc != null)
            {
                var active = refreshDoc.Status == "valid" && refreshDoc.ExpiresAt > DateTime.UtcNow;
                await context.Response.WriteAsJsonAsync(new
                {
                    active,
                    sub = refreshDoc.Subject,
                    client_id = app.ClientId,
                    scope = string.Join(" ", refreshDoc.Scopes),
                    token_type = "refresh_token",
                    exp = new DateTimeOffset(refreshDoc.ExpiresAt).ToUnixTimeSeconds(),
                    iat = new DateTimeOffset(refreshDoc.CreatedAt).ToUnixTimeSeconds(),
                });
                return;
            }
        }

        // Try as JWT access token
        if (tokenTypeHint is null or "access_token")
        {
            var signingKeyService = context.RequestServices.GetRequiredService<OidcSigningKeyService>();
            var issuer = $"{context.Request.Scheme}://{context.Request.Host}";

            var handler = new JsonWebTokenHandler();
            var validationResult = await handler.ValidateTokenAsync(token, new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = issuer,
                ValidateAudience = false,
                ValidateLifetime = false, // We check expiry ourselves to return active=false instead of error
                IssuerSigningKey = signingKeyService.GetSigningKey(),
            });

            if (validationResult.IsValid)
            {
                validationResult.Claims.TryGetValue("sub", out var subObj);
                validationResult.Claims.TryGetValue("scope", out var scopeObj);
                validationResult.Claims.TryGetValue("client_id", out var cidObj);
                validationResult.Claims.TryGetValue("exp", out var expObj);
                validationResult.Claims.TryGetValue("iat", out var iatObj);

                var isExpired = validationResult.SecurityToken is JsonWebToken jwt && jwt.ValidTo < DateTime.UtcNow;

                await context.Response.WriteAsJsonAsync(new
                {
                    active = !isExpired,
                    sub = subObj?.ToString(),
                    client_id = cidObj?.ToString() ?? app.ClientId,
                    scope = scopeObj?.ToString(),
                    token_type = "access_token",
                    exp = expObj,
                    iat = iatObj,
                });
                return;
            }
        }

        // Token not recognized — return inactive
        await context.Response.WriteAsJsonAsync(new { active = false });
    }
}
