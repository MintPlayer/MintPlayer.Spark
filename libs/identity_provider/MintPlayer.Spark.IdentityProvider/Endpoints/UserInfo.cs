using Microsoft.AspNetCore.Identity;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.IdentityProvider.Endpoints.Oidc;
using MintPlayer.Spark.IdentityProvider.Models;
using MintPlayer.Spark.IdentityProvider.Services;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;

namespace MintPlayer.Spark.IdentityProvider.Endpoints;

/// <summary>The userinfo endpoint (<c>GET /connect/userinfo</c>, OIDC Core §5.3).</summary>
/// <remarks>
/// <para>
/// Generic over the application's user type, closed once when the routes are mapped
/// (<see cref="OidcUserEndpoints"/>), so the user is loaded through a typed
/// <see cref="UserManager{TUser}"/>. The request-time "Identity not configured" 500 it used to answer
/// when no user type was registered is gone: startup now refuses that configuration.
/// </para>
/// <para>
/// Raw: its only input is the bearer token in the <c>Authorization</c> header, which no
/// <c>[RouteParam]</c>/<c>[QueryParam]</c> binds.
/// </para>
/// </remarks>
[MemberOf<OidcConnectCorsGroup>]
internal sealed partial class OidcUserInfo<TUser> : IGetEndpoint
    where TUser : SparkUser, new()
{
    public static string Path => "/userinfo";

    [Inject] private readonly UserManager<TUser> userManager;
    [Inject] private readonly IDocumentStore store;
    [Inject] private readonly OidcKeyRing signingKeyService;
    [Inject] private readonly OidcIssuer oidcIssuer;
    [Inject] private readonly OidcProofOfPossession proofOfPossession;
    [Inject] private readonly OidcJwe jwe;

    public async Task<IResult> HandleAsync(HttpContext context)
    {
        var ct = context.RequestAborted;

        // Bearer, or DPoP for a DPoP-bound token (RFC 9449 §7.1).
        var authHeader = context.Request.Headers.Authorization.FirstOrDefault();
        string? scheme = null;
        if (authHeader?.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) == true) scheme = "Bearer";
        else if (authHeader?.StartsWith("DPoP ", StringComparison.OrdinalIgnoreCase) == true) scheme = "DPoP";
        if (scheme is null)
        {
            context.Response.Headers["WWW-Authenticate"] = "Bearer, DPoP";
            return Results.Json(new { error = "invalid_token" }, statusCode: 401);
        }

        var accessToken = authHeader![(scheme.Length + 1)..];
        var issuer = oidcIssuer.Resolve(context.Request);

        using var session = store.OpenAsyncSession();

        // Signature and expiry alone cannot tell that a token was revoked, so this endpoint
        // went on serving a revoked token's claims for the rest of its lifetime.
        var resolved = await AccessTokens.ResolveAsync(session, signingKeyService, accessToken, issuer, ct);
        if (resolved is not { IsActive: true, Record: { } record })
        {
            context.Response.Headers["WWW-Authenticate"] = $"{scheme} error=\"invalid_token\"";
            return Results.Json(new { error = "invalid_token" }, statusCode: 401);
        }

        // D8: a sender-constrained token is only good with its proof.
        if (resolved.ConfirmationJkt is { } jkt)
        {
            var proof = context.Request.Headers[OidcProofOfPossession.DpopHeader].ToString();
            var (proofKey, _) = scheme == "DPoP" && proof.Length > 0
                ? await proofOfPossession.ValidateProofAsync(context, proof, accessToken, ct)
                : (null, "missing");
            if (proofKey != jkt)
            {
                context.Response.Headers["WWW-Authenticate"] = "DPoP error=\"invalid_dpop_proof\"";
                return Results.Json(new { error = "invalid_dpop_proof" }, statusCode: 401);
            }
        }
        if (resolved.ConfirmationX5t is { } x5t
            && (await context.Connection.GetClientCertificateAsync(ct) is not { } certificate || OidcProofOfPossession.Thumbprint(certificate) != x5t))
        {
            return Results.Json(new { error = "invalid_token", error_description = "The token is bound to another certificate." }, statusCode: 401);
        }

        // The record's subject is the user id; the token's `sub` may be pairwise.
        var user = await userManager.FindByIdAsync(record.Subject);
        var app = await session.LoadAsync<OidcApplication>(record.ApplicationId, ct);
        if (user == null || app == null)
        {
            return Results.Json(new { error = "invalid_token", error_description = "User not found." }, statusCode: 401);
        }

        var scopeNames = (resolved.Scope ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        var grantedScopes = await OidcScopeCatalog.LoadAsync(session, scopeNames, ct);

        var claims = OidcTokenGenerator.ResolveUserInfoClaims(user, grantedScopes);
        claims["sub"] = OidcSubjects.For(app, user.Id!);

        // OIDC Core §5.3.2: plain JSON, or a JWT when the client registered a signing or encryption algorithm.
        if (string.IsNullOrEmpty(app.UserinfoSignedResponseAlg) && string.IsNullOrEmpty(app.UserinfoEncryptedResponseAlg))
            return Results.Json(claims);

        var signed = new Microsoft.IdentityModel.JsonWebTokens.JsonWebTokenHandler().CreateToken(new Microsoft.IdentityModel.Tokens.SecurityTokenDescriptor
        {
            Claims = claims,
            Issuer = issuer,
            Audience = app.ClientId,
            IssuedAt = DateTime.UtcNow,
            SigningCredentials = signingKeyService.GetSigningCredentials(app.UserinfoSignedResponseAlg),
        });
        var jwt = await jwe.EncryptAsync(signed, app, app.UserinfoEncryptedResponseAlg, app.UserinfoEncryptedResponseEnc, ct);
        if (jwt is null)
            return Results.Json(new { error = "invalid_client", error_description = "The client asked for encrypted userinfo and has no usable encryption key." }, statusCode: 400);
        return Results.Text(jwt, "application/jwt");
    }
}

/// <summary>The userinfo POST form: the optional <c>access_token</c> field (RFC 6750 §2.2).</summary>
internal sealed record OidcUserInfoForm(string? AccessToken);

/// <summary>
/// The userinfo endpoint by POST (OIDC Core §5.3.1: the endpoint "MUST support the use of the HTTP GET and HTTP POST
/// methods"), with the token in the Authorization header as for GET. Found by the OpenID conformance suite
/// (oidcc-userinfo-post-header), which the GET-only endpoint answered 405.
/// </summary>
/// <remarks>
/// A relying party's back channel, which carries no cookie, so no antiforgery token: the exemption is stated.
/// </remarks>
[MemberOf<OidcConnectCorsGroup>]
internal sealed partial class OidcUserInfoByPost<TUser> : IPostEndpoint<OidcUserInfoForm>
    where TUser : SparkUser, new()
{
    public static string Path => "/userinfo";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.WithMetadata(new Microsoft.AspNetCore.Antiforgery.RequireAntiforgeryTokenAttribute(false));

    [Inject] private readonly UserManager<TUser> userManager;
    [Inject] private readonly IDocumentStore store;
    [Inject] private readonly OidcKeyRing signingKeyService;
    [Inject] private readonly OidcIssuer oidcIssuer;
    [Inject] private readonly OidcProofOfPossession proofOfPossession;
    [Inject] private readonly OidcJwe jwe;

    private HttpContext context = null!;

    /// <summary>RFC 6750 §2.2: the token may also travel as the <c>access_token</c> form field, without an Authorization header.</summary>
    protected override async ValueTask<OidcUserInfoForm?> BindRequestAsync(HttpContext context)
    {
        this.context = context;
        if (!string.IsNullOrEmpty(context.Request.Headers.Authorization) || !context.Request.HasFormContentType)
            return new OidcUserInfoForm(null);
        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        return new OidcUserInfoForm(form["access_token"].FirstOrDefault());
    }

    public override Task<IResult> HandleAsync(OidcUserInfoForm request, CancellationToken ct)
    {
        if (request.AccessToken is { Length: > 0 } token)
            context.Request.Headers.Authorization = "Bearer " + token;
        return new OidcUserInfo<TUser>(userManager, store, signingKeyService, oidcIssuer, proofOfPossession, jwe).HandleAsync(context);
    }
}
