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
    [Inject] private readonly OidcSigningKeyService signingKeyService;
    [Inject] private readonly OidcIssuer oidcIssuer;

    public async Task<IResult> HandleAsync(HttpContext context)
    {
        var ct = context.RequestAborted;

        // Extract Bearer token from Authorization header
        var authHeader = context.Request.Headers.Authorization.FirstOrDefault();
        if (string.IsNullOrEmpty(authHeader) || !authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.Headers["WWW-Authenticate"] = "Bearer";
            return Results.Json(new { error = "invalid_token" }, statusCode: 401);
        }

        var accessToken = authHeader["Bearer ".Length..];

        // Validate the access token JWT
        var issuer = oidcIssuer.Resolve(context.Request);

        using var session = store.OpenAsyncSession();

        // Signature and expiry alone cannot tell that a token was revoked, so this endpoint
        // went on serving a revoked token's claims for the rest of its lifetime.
        var resolved = await AccessTokens.ResolveAsync(session, signingKeyService, accessToken, issuer, ct);

        if (resolved is not { IsActive: true })
        {
            context.Response.Headers["WWW-Authenticate"] = "Bearer error=\"invalid_token\"";
            return Results.Json(new { error = "invalid_token" }, statusCode: 401);
        }

        var subject = resolved.Subject;
        var scopeString = resolved.Scope ?? "";

        if (string.IsNullOrEmpty(subject))
        {
            return Results.Json(new { error = "invalid_token", error_description = "Missing subject claim." }, statusCode: 401);
        }

        // Load user
        var user = await userManager.FindByIdAsync(subject);
        if (user == null)
        {
            return Results.Json(new { error = "invalid_token", error_description = "User not found." }, statusCode: 401);
        }

        // Load scope definitions from DB to resolve claims
        var scopeNames = scopeString.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();

        var grantedScopes = await OidcScopeCatalog.LoadAsync(session, scopeNames, ct);

        // Resolve claims from scope definitions
        var claims = OidcTokenGenerator.ResolveUserInfoClaims(user, grantedScopes);

        return Results.Json(claims);
    }
}
