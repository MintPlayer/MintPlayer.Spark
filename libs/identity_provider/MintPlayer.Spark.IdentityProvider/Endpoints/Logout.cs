using System.Text;
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
    /// <summary>The RP's preferred languages for this page (RP-Initiated Logout 1.0 §2); only selects a supported culture.</summary>
    [QueryParam("ui_locales")] public string? UiLocales { get; set; }

    [Inject] private readonly SignInManager<TUser> signInManager;
    [Inject] private readonly OidcKeyRing signingKeyService;
    [Inject] private readonly OidcSessionStore sessions;
    [Inject] private readonly OidcIssuer oidcIssuer;
    [Inject] private readonly IDocumentStore store;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;
    [Inject] private readonly ConnectText text;
    [Inject] private readonly Configuration.SparkIdentityProviderOptions options;

    public override async Task<IResult> HandleAsync(CancellationToken ct)
    {
        var context = httpContextAccessor.HttpContext!;
        var postLogoutRedirectUri = PostLogoutRedirectUri;
        text.UseUiLocales(UiLocales);

        // I10: end the provider session first, while its cookie still says which one it is: refresh
        // tokens revoked, back-channel logout tokens sent, front-channel URIs collected.
        var signedIn = await OidcInteractiveSession.ReadAsync(context);
        var ended = await sessions.EndAsync(signedIn?.SessionId, oidcIssuer.Resolve(context.Request), ct);

        // Sign out the user if authenticated
        if (signedIn is not null || context.User?.Identity?.IsAuthenticated == true)
            await signInManager.SignOutAsync();

        if (string.IsNullOrEmpty(postLogoutRedirectUri))
            return ConnectResults.Html(SignedOutPage(context, ended.FrontChannelUris, continueTo: null));

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
                return ConnectResults.ErrorPage(context, text, options.Branding, StatusCodes.Status400BadRequest,
                    "logoutInvalidIdTokenHint", headingKey: "logoutInvalidIdTokenHintTitle");
            }

            clientId = hinted;
        }

        var app = string.IsNullOrEmpty(clientId)
            ? null
            : await OidcAuthorizationFlow.FindApplicationByClientIdAsync(session, clientId, ct);

        if (app is not { Enabled: true }
            || !app.PostLogoutRedirectUris.Contains(postLogoutRedirectUri, StringComparer.Ordinal))
        {
            return ConnectResults.ErrorPage(context, text, options.Branding, StatusCodes.Status400BadRequest,
                "logoutInvalidRedirectUri", headingKey: "logoutInvalidRedirectUriTitle");
        }

        var destination = RedirectUrl.With(postLogoutRedirectUri, ("state", State));
        // Front-channel logout: the clients' logout pages load in hidden iframes first, then the
        // browser continues. Without any, straight on.
        return ended.FrontChannelUris.Count == 0
            ? Results.Redirect(destination)
            : ConnectResults.Html(SignedOutPage(context, ended.FrontChannelUris, destination));
    }

    /// <summary>The signed-out page, which also carries the front-channel logout iframes (best effort, Front-Channel Logout 1.0 §4).</summary>
    private string SignedOutPage(HttpContext context, IReadOnlyList<string> frontChannelUris, string? continueTo)
    {
        var sb = new StringBuilder();
        ConnectPageTheme.AppendDocumentStart(sb, context, text["logoutTitle"], text.Culture, options.Branding);
        sb.Append("body{max-width:480px;margin:80px auto;padding:0 20px}iframe{display:none}");
        sb.Append("</style>");
        // Moves on after the iframes had their chance.
        if (continueTo is not null)
            sb.Append("<meta http-equiv=\"refresh\" content=\"2;url=").Append(ConnectPage.Encode(continueTo)).Append("\">");
        sb.Append("</head><body>");
        ConnectPageTheme.AppendBrand(sb, options.Branding);
        sb.Append("<h2>").Append(ConnectPage.Encode(text["logoutHeading"])).Append("</h2>");
        sb.Append("<p>").Append(ConnectPage.Encode(text[continueTo is null ? "logoutClose" : "logoutContinuing"])).Append("</p>");
        foreach (var uri in frontChannelUris)
            sb.Append("<iframe src=\"").Append(ConnectPage.Encode(uri)).Append("\"></iframe>");
        if (continueTo is not null)
            sb.Append("<p><a href=\"").Append(ConnectPage.Encode(continueTo)).Append("\">").Append(ConnectPage.Encode(text["deviceContinue"])).Append("</a></p>");
        sb.Append("</body></html>");
        return sb.ToString();
    }
}
