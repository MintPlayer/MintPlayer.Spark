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

        // token_type_hint is advisory only. RFC 7662 §2.1 requires the search to extend to the
        // other token types when the hinted one does not resolve, so it must not gate a branch:
        // gating it meant a live access token presented with token_type_hint=refresh_token came
        // back inactive, a false negative on a perfectly good token.

        // Try refresh token first — point-load by the hash of the presented value.
        var refreshDoc = await session
            .LoadAsync<OidcToken>(OidcTokenReference.DocumentId(token), ct);
        if (refreshDoc is not { Type: "refresh_token" })
            refreshDoc = null;

        if (refreshDoc != null)
        {
            // Authenticating as *some* client is not authority over another client's tokens.
            if (!OwnedBy(refreshDoc.ApplicationId, app))
            {
                await WriteInactiveAsync(context);
                return;
            }

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

        // Try as JWT access token
        var signingKeyService = context.RequestServices.GetRequiredService<OidcSigningKeyService>();
        var issuer = OidcIssuer.Resolve(context);

        var resolved = await AccessTokens.ResolveAsync(session, signingKeyService, token, issuer, ct);
        if (resolved != null)
        {
            if (resolved.Record == null || !OwnedBy(resolved.Record.ApplicationId, app))
            {
                await WriteInactiveAsync(context);
                return;
            }

            resolved.Claims.TryGetValue("exp", out var expObj);
            resolved.Claims.TryGetValue("iat", out var iatObj);

            // active reflects the database, not merely the signature. Reporting a revoked
            // token as active is precisely the failure RFC 7662 exists to prevent.
            await context.Response.WriteAsJsonAsync(new
            {
                active = resolved.IsActive,
                sub = resolved.Subject,
                client_id = resolved.ClientId ?? app.ClientId,
                // Without aud a resource server cannot answer "was this minted for me?" —
                // AccessTokens deliberately does not validate audience, so this is the only
                // channel through which the caller can check it.
                aud = resolved.Audiences,
                scope = resolved.Scope,
                token_type = "access_token",
                exp = expObj,
                iat = iatObj,
            });
            return;
        }

        // Token not recognized — return inactive
        await WriteInactiveAsync(context);
    }

    /// <summary>
    /// Whether the token record belongs to the client asking about it.
    /// <para>
    /// Introspection had no such check: any enabled client holding valid credentials could
    /// present a token it had come across and read back the subject and scopes of whoever it
    /// actually belonged to. Since each resource server is its own application, that let one
    /// resource server enumerate another's users. <c>Revocation</c> has always gated on this;
    /// introspection simply never did.
    /// </para>
    /// </summary>
    private static bool OwnedBy(string applicationId, OidcApplication app)
        => string.Equals(applicationId, app.Id, StringComparison.Ordinal);

    /// <summary>
    /// RFC 7662 does not require saying <em>why</em> a token is inactive, and saying so would
    /// separate "not yours" from "never issued" — an oracle. One shape for every negative.
    /// </summary>
    private static Task WriteInactiveAsync(HttpContext context)
        => context.Response.WriteAsJsonAsync(new { active = false });
}
