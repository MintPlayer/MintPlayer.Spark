using MintPlayer.Spark.IdentityProvider.Services;
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
        // token_type_hint is accepted and ignored: both token types are searched regardless
        // (see below), so the hint can only ever be an optimisation we decline to take.
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

        // Point-load by the hash of the presented value. Revoking through an
        // eventually-consistent index could miss a token issued moments earlier and report
        // success while leaving it live.
        var tokenDoc = await session.LoadAsync<OidcToken>(OidcTokenReference.DocumentId(token), ct);

        // An access token is a JWT, so the presented value is not itself the handle — its jti
        // is. Without this branch, revoking an access token silently did nothing: the lookup
        // above could never hit, and RFC 7009 mandates 200 either way, so the caller was told
        // it had succeeded.
        //
        // token_type_hint deliberately does not gate this. RFC 7009 §2.1 requires extending the
        // search when the hinted type does not resolve, and here the cost of obeying the hint
        // was silence: an access token revoked with token_type_hint=refresh_token matched
        // nothing, was never revoked, and still answered 200. A caller acting on a breach would
        // be told the credential was dead while it stayed live for its full lifetime.
        if (tokenDoc == null)
        {
            var signingKeyService = context.RequestServices.GetRequiredService<OidcSigningKeyService>();
            var issuer = OidcIssuer.Resolve(context);
            var resolved = await AccessTokens.ResolveAsync(session, signingKeyService, token, issuer, ct);
            tokenDoc = resolved?.Record;
        }

        if (tokenDoc is not { Status: "valid" })
            tokenDoc = null;

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
