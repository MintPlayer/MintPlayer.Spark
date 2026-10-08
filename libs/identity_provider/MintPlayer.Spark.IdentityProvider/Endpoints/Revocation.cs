using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.IdentityProvider.Endpoints.Oidc;
using MintPlayer.Spark.IdentityProvider.Models;
using MintPlayer.Spark.IdentityProvider.Services;
using Raven.Client.Documents;

namespace MintPlayer.Spark.IdentityProvider.Endpoints;

/// <summary>
/// A client presenting a token: the body of revocation (RFC 7009 §2.1) and introspection
/// (RFC 7662 §2.1), both <c>application/x-www-form-urlencoded</c> and authenticated with
/// <c>client_secret_post</c>. Every member is optional here; which are required is each endpoint's
/// own check, answered in its protocol's shape.
/// </summary>
internal sealed record OidcClientTokenRequest(string? Token, string? TokenTypeHint, string? ClientId, string? ClientSecret)
{
    /// <summary>
    /// Reads the form, kept for both endpoints so they cannot drift apart. A body that is not a form
    /// is refused through <c>OnBindFailedAsync</c> (<see cref="BindFailed"/>).
    /// </summary>
    public static async ValueTask<OidcClientTokenRequest?> BindAsync(HttpContext context)
    {
        if (!context.Request.HasFormContentType)
            throw new EndpointBindingException(StatusCodes.Status400BadRequest, "Content-Type must be application/x-www-form-urlencoded.");

        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        return new OidcClientTokenRequest(
            Token: form["token"].FirstOrDefault(),
            TokenTypeHint: form["token_type_hint"].FirstOrDefault(),
            ClientId: form["client_id"].FirstOrDefault(),
            ClientSecret: form["client_secret"].FirstOrDefault());
    }

    /// <summary>
    /// The refusal for a body that could not be read: the bare RFC 6749 §5.2 <c>invalid_request</c>,
    /// with no description, exactly as both handlers answered before they were typed.
    /// </summary>
    public static ValueTask<IResult> BindFailed()
        => new(Results.Json(new { error = "invalid_request" }, statusCode: StatusCodes.Status400BadRequest));
}

/// <summary>Token revocation (RFC 7009), <c>POST /connect/revoke</c>.</summary>
/// <remarks>
/// ⚠️ Deliberately NOT antiforgery-protected: a machine endpoint authenticated by client
/// credentials, never by an ambient cookie (see <see cref="OidcConnectCorsGroup"/>).
/// </remarks>
[MemberOf<OidcConnectCorsGroup>]
internal sealed partial class OidcRevoke : IPostEndpoint<OidcClientTokenRequest>
{
    public static string Path => "/revoke";

    [Inject] private readonly IDocumentStore store;
    [Inject] private readonly OidcKeyRing signingKeyService;
    [Inject] private readonly OidcIssuer oidcIssuer;
    [Inject] private readonly OidcClientAuthenticator clientAuthenticator;

    /// <summary>Kept from <see cref="BindRequestAsync"/> (D8) for the issuer; the endpoint is created per request.</summary>
    private HttpContext httpContext = null!;

    protected override ValueTask<OidcClientTokenRequest?> BindRequestAsync(HttpContext context)
    {
        httpContext = context;
        return OidcClientTokenRequest.BindAsync(context);
    }

    protected override ValueTask<IResult> OnBindFailedAsync(HttpContext context, EndpointBindingException? failure)
        => OidcClientTokenRequest.BindFailed();

    public override async Task<IResult> HandleAsync(OidcClientTokenRequest request, CancellationToken ct)
    {
        var token = request.Token;
        // token_type_hint is accepted and ignored: both token types are searched regardless
        // (see below), so the hint can only ever be an optimisation we decline to take.

        if (string.IsNullOrEmpty(token))
        {
            return Results.Json(new { error = "invalid_request", error_description = "token is required." }, statusCode: 400);
        }

        using var session = store.OpenAsyncSession();

        // Authenticate client (D8). A public client may revoke its own tokens (RFC 7009 §2.1).
        var client = await clientAuthenticator.AuthenticateAsync(httpContext, httpContext.Request.Form, session, ct);
        if (!client.Succeeded)
            return client.ToResult(httpContext);
        var app = client.Application!;

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
            var issuer = oidcIssuer.Resolve(httpContext.Request);
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
        return Results.Ok();
    }
}
