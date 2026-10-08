using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.IdentityProvider.Endpoints.Oidc;
using MintPlayer.Spark.IdentityProvider.Models;
using MintPlayer.Spark.IdentityProvider.Services;
using Raven.Client.Documents;

namespace MintPlayer.Spark.IdentityProvider.Endpoints;

/// <summary>
/// Token introspection (RFC 7662), <c>POST /connect/introspect</c>: lets resource servers validate
/// tokens and retrieve their claims.
/// </summary>
/// <remarks>
/// ⚠️ Deliberately NOT a member of <see cref="OidcConnectCorsGroup"/> — introspection never carried
/// the dynamic-CORS convention, unlike its neighbours in that group.
/// <para>
/// Also deliberately not antiforgery-protected: this is a machine endpoint authenticated by client
/// credentials, never by an ambient cookie, so there is no ambient authority for a cross-site
/// request to borrow.
/// </para>
/// </remarks>
[MemberOf<OidcConnectGroup>]
internal sealed partial class OidcIntrospect : IPostEndpoint<OidcClientTokenRequest>
{
    public static string Path => "/introspect";

    [Inject] private readonly IDocumentStore store;
    [Inject] private readonly OidcKeyRing signingKeyService;
    [Inject] private readonly OidcIssuer oidcIssuer;
    [Inject] private readonly OidcClientAuthenticator clientAuthenticator;

    /// <summary>Kept from <see cref="BindRequestAsync"/> (D8) for the issuer; the endpoint is created per request.</summary>
    private HttpContext httpContext = null!;

    /// <summary>The form the binder read, kept for client authentication (which needs the raw fields).</summary>
    private IFormCollection form = null!;

    protected override async ValueTask<OidcClientTokenRequest?> BindRequestAsync(HttpContext context)
    {
        httpContext = context;
        var request = await OidcClientTokenRequest.BindAsync(context);
        // Read once by the binder above; ReadFormAsync returns the cached collection.
        form = await context.Request.ReadFormAsync(context.RequestAborted);
        return request;
    }

    protected override ValueTask<IResult> OnBindFailedAsync(HttpContext context, EndpointBindingException? failure)
        => OidcClientTokenRequest.BindFailed();

    public override async Task<IResult> HandleAsync(OidcClientTokenRequest request, CancellationToken ct)
    {
        var token = request.Token;

        if (string.IsNullOrEmpty(token))
        {
            return Results.Json(new { error = "invalid_request", error_description = "token is required." }, statusCode: 400);
        }

        using var session = store.OpenAsyncSession();

        // Authenticate client (D8). Introspection discloses a token's subject and scopes, so a public
        // client, which proves nothing about who it is, may not use it.
        var client = await clientAuthenticator.AuthenticateAsync(httpContext, form, session, ct);
        if (!client.Succeeded)
            return client.ToResult(httpContext);
        if (client.Method == OidcClientAuthMethods.None)
            return Results.Json(new { error = "invalid_client" }, statusCode: 401);
        var app = client.Application!;

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
            // A refresh token carries no audience, so ownership is the only basis for reading it.
            if (!OwnedBy(refreshDoc.ApplicationId, app) && !app.MayIntrospectAnyAudience)
                return Inactive();

            var active = refreshDoc.Status == "valid" && refreshDoc.ExpiresAt > DateTime.UtcNow;
            return Results.Json(new
            {
                active,
                sub = refreshDoc.Subject,
                client_id = app.ClientId,
                scope = string.Join(" ", refreshDoc.Scopes),
                token_type = "refresh_token",
                exp = new DateTimeOffset(refreshDoc.ExpiresAt).ToUnixTimeSeconds(),
                iat = new DateTimeOffset(refreshDoc.CreatedAt).ToUnixTimeSeconds(),
            });
        }

        // Try as JWT access token
        var issuer = oidcIssuer.Resolve(httpContext.Request);

        var resolved = await AccessTokens.ResolveAsync(session, signingKeyService, token, issuer, ct);
        if (resolved != null)
        {
            if (resolved.Record == null || !MayIntrospect(resolved, app))
                return Inactive();

            resolved.Claims.TryGetValue("exp", out var expObj);
            resolved.Claims.TryGetValue("iat", out var iatObj);

            // active reflects the database, not merely the signature. Reporting a revoked
            // token as active is precisely the failure RFC 7662 exists to prevent.
            var answer = new Dictionary<string, object?>
            {
                ["active"] = resolved.IsActive,
                ["iss"] = issuer,
                ["sub"] = resolved.Subject,
                ["client_id"] = resolved.ClientId ?? app.ClientId,
                // Without aud a resource server cannot answer "was this minted for me?" —
                // AccessTokens deliberately does not validate audience, so this is the only
                // channel through which the caller can check it.
                ["aud"] = resolved.Audiences,
                ["scope"] = resolved.Scope,
                ["token_type"] = "access_token",
                ["exp"] = expObj,
                ["iat"] = iatObj,
            };
            // I12: the binding (RFC 7662 §2.2 allows any token claim) so the resource server can demand the
            // proof, and the groups so security.json governs the caller as it would with the JWT itself.
            foreach (var name in new[] { "cnf", "group", "groups", "act" })
            {
                if (resolved.Claims.TryGetValue(name, out var value))
                    answer[name] = value;
            }
            return Results.Json(answer);
        }

        // Token not recognized — return inactive
        return Inactive();
    }

    /// <summary>
    /// Whether the token record belongs to the client asking about it.
    /// <para>
    /// Introspection had no such check: any enabled client holding valid credentials could
    /// present a token it had come across and read back the subject and scopes of whoever it
    /// actually belonged to. Since each resource server is its own application, that let one
    /// resource server enumerate another's users. Revocation has always gated on this;
    /// introspection simply never did.
    /// </para>
    /// </summary>
    private static bool OwnedBy(string applicationId, OidcApplication app)
        => string.Equals(applicationId, app.Id, StringComparison.Ordinal);

    /// <summary>
    /// Whether this caller may see an access token's claims: it issued the token, or the token
    /// was minted <em>for</em> it, or it is a gateway that has opted out of the restriction.
    /// <para>
    /// The audience arm is not a loosening — it repairs one. Gating on ownership alone (the N1
    /// fix) also refused the deployment RFC 7662 is written for, where a resource server
    /// introspects tokens it is meant to accept: those are issued to some *client*, so the
    /// resource server never owns them and could never ask about them. Audience is what makes a
    /// resource server the legitimate reader of a token it did not issue.
    /// </para>
    /// <para>
    /// It is also where audience finally means something. Nothing else in the package enforces
    /// <c>aud</c> — a verifier checking only <c>active</c> would accept a token minted for
    /// somewhere else — so here the token has to name the caller before its claims are handed
    /// over.
    /// </para>
    /// </summary>
    private static bool MayIntrospect(ResolvedAccessToken resolved, OidcApplication app)
        => OwnedBy(resolved.Record!.ApplicationId, app)
        || resolved.Audiences.Contains(app.ClientId, StringComparer.Ordinal)
        || app.MayIntrospectAnyAudience;

    /// <summary>
    /// RFC 7662 does not require saying <em>why</em> a token is inactive, and saying so would
    /// separate "not yours" from "never issued" — an oracle. One shape for every negative.
    /// </summary>
    private static IResult Inactive() => Results.Json(new { active = false });
}
