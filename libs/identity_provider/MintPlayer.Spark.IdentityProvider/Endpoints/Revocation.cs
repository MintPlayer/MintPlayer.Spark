using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.IdentityProvider.Indexes;
using MintPlayer.Spark.IdentityProvider.Models;
using Raven.Client.Documents;

namespace MintPlayer.Spark.IdentityProvider.Endpoints;

/// <summary>
/// Token Revocation Endpoint (RFC 7009).
/// Allows clients to revoke refresh tokens and access tokens.
/// </summary>
internal static class Revocation
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

        // Try to find token by ReferenceId (refresh tokens, auth codes)
        var tokenDoc = await session
            .Query<OidcToken, OidcTokens_ByReferenceId>()
            .Where(t => t.ReferenceId == token && t.Status == "valid")
            .FirstOrDefaultAsync(ct);

        if (tokenDoc != null && tokenDoc.ApplicationId == app.Id)
        {
            tokenDoc.Status = "revoked";
            tokenDoc.RedeemedAt = DateTime.UtcNow;

            // If revoking a refresh token, also revoke associated access tokens
            if (tokenDoc.Type == "refresh_token" && !string.IsNullOrEmpty(tokenDoc.AuthorizationId))
            {
                var associatedTokens = await session
                    .Query<OidcToken>()
                    .Where(t => t.AuthorizationId == tokenDoc.AuthorizationId && t.Type == "access_token" && t.Status == "valid")
                    .ToListAsync(ct);

                foreach (var at in associatedTokens)
                {
                    at.Status = "revoked";
                    at.RedeemedAt = DateTime.UtcNow;
                }
            }

            await session.SaveChangesAsync(ct);
        }

        // Per RFC 7009: always return 200 OK, even if token was not found
        context.Response.StatusCode = 200;
    }
}
