using Microsoft.AspNetCore.Identity;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.IdentityProvider.Endpoints.Oidc;
using MintPlayer.Spark.IdentityProvider.Services;
using Raven.Client.Documents;

namespace MintPlayer.Spark.IdentityProvider.Endpoints;

/// <summary>The end-session endpoint (<c>GET /connect/logout</c>, OIDC RP-Initiated Logout 1.0).</summary>
/// <remarks>
/// Generic over the application's user type, closed once when the routes are mapped
/// (<see cref="OidcUserEndpoints"/>), so it signs out through a typed <see cref="SignInManager{TUser}"/>
/// instead of resolving and reflecting on one per request.
/// </remarks>
[MemberOf<OidcConnectGroup>]
internal sealed partial class OidcLogout<TUser> : IGetEndpoint<string>
    where TUser : SparkUser, new()
{
    public static string Path => "/logout";

    [QueryParam("post_logout_redirect_uri")] public string? PostLogoutRedirectUri { get; set; }
    [QueryParam("state")] public string? State { get; set; }
    [QueryParam("client_id")] public string? ClientId { get; set; }
    [QueryParam("id_token_hint")] public string? IdTokenHint { get; set; }

    [Inject] private readonly SignInManager<TUser> signInManager;
    [Inject] private readonly OidcSigningKeyService signingKeyService;
    [Inject] private readonly OidcIssuer oidcIssuer;
    [Inject] private readonly IDocumentStore store;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    public override async Task<IResult> HandleAsync(CancellationToken ct)
    {
        var context = httpContextAccessor.HttpContext!;
        var postLogoutRedirectUri = PostLogoutRedirectUri;

        // Sign out the user if authenticated
        if (context.User?.Identity?.IsAuthenticated == true)
            await signInManager.SignOutAsync();

        if (string.IsNullOrEmpty(postLogoutRedirectUri))
            return ConnectResults.Text(200, "<html><body><h2>You have been signed out.</h2><p>You may close this window.</p></body></html>", "text/html");

        // Validate post_logout_redirect_uri against registered client URIs
        using var session = store.OpenAsyncSession();

        // The URI must be registered by *the client asking*, which means the request has to
        // say who that is. Validating against every enabled application instead — as this
        // did — makes one client's registered URI a legal logout destination for every
        // other client, so anyone who can register an application gains a redirect through
        // this provider's origin for all of them. client_id is how RP-initiated logout
        // identifies the caller (OIDC RP-Initiated Logout 1.0 §2).
        //
        // id_token_hint identifies it too, and is what the stock ASP.NET OpenIdConnect handler sends
        // instead (#490 M6). The hint is only trusted when this provider signed it (OidcIdTokenHint);
        // a hint naming a different client than client_id is a contradiction and is refused.
        var clientId = ClientId;
        if (!string.IsNullOrEmpty(IdTokenHint))
        {
            var hinted = await OidcIdTokenHint.ResolveClientIdAsync(signingKeyService, IdTokenHint, oidcIssuer.Resolve(context.Request));
            if (hinted is null
                || (!string.IsNullOrEmpty(clientId) && !string.Equals(clientId, hinted, StringComparison.Ordinal)))
            {
                return ConnectResults.Text(400, "<html><body><h2>Invalid id_token_hint</h2><p>The provided id_token_hint was not issued by this provider to this client.</p></body></html>", "text/html");
            }

            clientId = hinted;
        }

        var app = string.IsNullOrEmpty(clientId)
            ? null
            : await OidcAuthorizationFlow.FindApplicationByClientIdAsync(session, clientId, ct);

        if (app is not { Enabled: true }
            || !app.PostLogoutRedirectUris.Contains(postLogoutRedirectUri, StringComparer.Ordinal))
        {
            return ConnectResults.Text(400, "<html><body><h2>Invalid post_logout_redirect_uri</h2><p>The provided redirect URI is not registered for this client.</p></body></html>", "text/html");
        }

        return Results.Redirect(RedirectUrl.With(postLogoutRedirectUri, ("state", State)));
    }
}
