using Microsoft.AspNetCore.WebUtilities;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.IdentityProvider.Configuration;
using MintPlayer.Spark.IdentityProvider.Endpoints.Oidc;
using MintPlayer.Spark.IdentityProvider.Models;
using MintPlayer.Spark.IdentityProvider.Services;
using Raven.Client;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.IdentityProvider.Endpoints;

/// <summary>The authorization endpoint (<c>GET /connect/authorize</c>).</summary>
/// <remarks>
/// Every query value is optional at the binding level: a missing one is answered by this endpoint
/// in RFC 6749 §4.1.2.1's shape, never by the binder's problem details.
/// </remarks>
[MemberOf<OidcConnectGroup>]
internal sealed partial class OidcAuthorize : IGetEndpoint<string>
{
    public static string Path => "/authorize";

    [QueryParam("client_id")] public string? ClientId { get; set; }
    [QueryParam("redirect_uri")] public string? RedirectUri { get; set; }
    [QueryParam("response_type")] public string? ResponseType { get; set; }
    [QueryParam("scope")] public string? Scope { get; set; }
    [QueryParam("state")] public string? State { get; set; }
    [QueryParam("code_challenge")] public string? CodeChallenge { get; set; }
    [QueryParam("code_challenge_method")] public string? CodeChallengeMethod { get; set; }
    [QueryParam("nonce")] public string? Nonce { get; set; }

    [Inject] private readonly IDocumentStore store;
    [Inject] private readonly SparkIdentityProviderOptions options;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    public override async Task<IResult> HandleAsync(CancellationToken ct)
    {
        var context = httpContextAccessor.HttpContext!;
        var clientId = ClientId;
        var redirectUri = RedirectUri;
        var scope = Scope;
        var state = State;
        var codeChallenge = CodeChallenge;

        // Validate required parameters
        if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(redirectUri) ||
            string.IsNullOrEmpty(ResponseType) || string.IsNullOrEmpty(scope))
        {
            return Results.Json(new { error = "invalid_request", error_description = "Missing required parameters." }, statusCode: 400);
        }

        if (ResponseType != "code")
        {
            return Results.Json(new { error = "unsupported_response_type", error_description = "Only 'code' response type is supported." }, statusCode: 400);
        }

        // Lookup client application
        using var session = store.OpenAsyncSession();

        var app = await OidcAuthorizationFlow.FindApplicationByClientIdAsync(session, clientId, ct);
        if (app == null || !app.Enabled)
        {
            return Results.Json(new { error = "invalid_client", error_description = "Unknown or disabled client." }, statusCode: 400);
        }

        // The client must be registered for this grant. Without it, a client provisioned
        // solely for client_credentials — a machine identity, typically holding broader
        // application claims than any user — could still be driven through the interactive
        // flow.
        if (!app.AllowedGrantTypes.Contains("authorization_code", StringComparer.OrdinalIgnoreCase))
        {
            return Results.Json(new { error = "unauthorized_client", error_description = "This client is not authorized for authorization_code grant." }, statusCode: 400);
        }

        // Validate redirect URI
        if (!app.RedirectUris.Contains(redirectUri, StringComparer.Ordinal))
        {
            return Results.Json(new { error = "invalid_request", error_description = "Invalid redirect_uri." }, statusCode: 400);
        }

        // Validate PKCE
        if (app.RequirePkce && string.IsNullOrEmpty(codeChallenge))
        {
            return RedirectWithError(redirectUri, state, "invalid_request", "PKCE code_challenge is required.");
        }

        if (!string.IsNullOrEmpty(codeChallenge) && CodeChallengeMethod != "S256")
        {
            return RedirectWithError(redirectUri, state, "invalid_request", "Only S256 code_challenge_method is supported.");
        }

        // Validate requested scopes against BOTH sources of truth.
        //
        // The application's AllowedScopes says what this client may ask for; the OidcScope
        // documents say what the provider actually defines. Only the first was checked here,
        // while token issuance resolves against the second — so a scope listed on the client but
        // undefined (or disabled) was accepted, consented to, and carried on the code, and then
        // silently vanished from the issued token's `scope` claim. The user saw success at every
        // screen and got a token that authorized less than they granted. Rejecting here makes the
        // disagreement surface at the point of request instead.
        var requestedScopes = scope.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        var definedScopes = await Token.LoadScopesAsync(session, requestedScopes, ct);

        foreach (var s in requestedScopes)
        {
            if (!app.AllowedScopes.Contains(s, StringComparer.OrdinalIgnoreCase))
            {
                return RedirectWithError(redirectUri, state, "invalid_scope", $"Scope '{s}' is not allowed for this client.");
            }

            if (!definedScopes.Any(d => string.Equals(d.Name, s, StringComparison.OrdinalIgnoreCase)))
            {
                return RedirectWithError(redirectUri, state, "invalid_scope", $"Scope '{s}' is not available.");
            }
        }

        // Check user authentication
        var (userId, authTime) = await context.GetInteractiveUserAsync();
        if (string.IsNullOrEmpty(userId))
        {
            // User not authenticated — redirect to the login page.
            //
            // #490 M6: an external sign-in started from that page that was refused comes back here
            // with ?sparkExternalLogin=<code> when the challenge had no errorUrl. It is lifted out of
            // the pending authorize URL and handed to the login page, which shows it; left inside
            // returnUrl it was dropped on this bounce and the user saw the form again with no reason.
            // The query is rebuilt only then, so an ordinary bounce keeps the URL byte for byte.
            var pending = context.Request.QueryString.Value;
            var externalLogin = context.Request.Query[ExternalLoginQueryParameter].ToString();
            if (!string.IsNullOrEmpty(externalLogin))
            {
                pending = QueryString.Create(context.Request.Query
                    .Where(p => !string.Equals(p.Key, ExternalLoginQueryParameter, StringComparison.Ordinal))
                    .SelectMany(p => p.Value.Select(v => KeyValuePair.Create(p.Key, v)))).Value;
            }

            var loginUrl = $"/connect/login?returnUrl={Uri.EscapeDataString($"/connect/authorize{pending}")}";
            return Results.Redirect(string.IsNullOrEmpty(externalLogin)
                ? loginUrl
                : QueryHelpers.AddQueryString(loginUrl, ExternalLoginQueryParameter, externalLogin));
        }

        // Everything above validated the request against the application record. Persist that
        // verdict now and hand the browser nothing but an opaque handle to it, so no later hop
        // has to — or is able to — re-derive it from request input.
        var (request, requestId) = await CreateRequestAsync(
            session, app, userId, requestedScopes, redirectUri, state, codeChallenge, CodeChallengeMethod, Nonce, ct);
        request.AuthTime = authTime;

        // Load the user's standing grant once: both the implicit branch and the skip-consent
        // check below need it, and the implicit branch used to run without consulting it at all.
        var existingAuth = await session.LoadAsync<OidcAuthorization>(
            OidcAuthorizationReference.DocumentId(userId, app.Id!), ct);

        var withdrawn = existingAuth is not null && existingAuth.Status != "valid";

        // Auto-approval says "this user already trusts this client", which is exactly the
        // statement a withdrawal retracts. Skipping the screen here — the branch returned before
        // the Status check below ever ran — meant a withdrawn grant came back with no screen, no
        // click, and no human, on the client's next redirect. A user who has withdrawn must be
        // asked again, whatever the client's consent type.
        if (app.ConsentType == "implicit" && options.AutoApproveImplicitConsent && !withdrawn)
        {
            request.AuthorizationId = await OidcAuthorizationFlow.EnsureAuthorizationAsync(session, app, userId, requestedScopes, ct);
            return Results.Redirect(await OidcAuthorizationFlow.IssueCodeAsync(session, request, ct));
        }

        if (existingAuth is { Status: "valid" })
        {
            var allScopesCovered = requestedScopes.All(s =>
                existingAuth.GrantedScopes.Contains(s, StringComparer.OrdinalIgnoreCase));

            if (allScopesCovered)
            {
                request.AuthorizationId = existingAuth.Id!;
                return Results.Redirect(await OidcAuthorizationFlow.IssueCodeAsync(session, request, ct));
            }
        }

        await session.SaveChangesAsync(ct);
        return Results.Redirect($"/connect/consent?request_id={Uri.EscapeDataString(requestId)}");
    }

    /// <summary>
    /// Records a validated authorization request and returns it together with the handle the
    /// browser carries. The document is stored but not yet saved — the caller decides whether
    /// this request goes to a consent screen or straight to code issuance.
    /// </summary>
    private static async Task<(OidcAuthorizationRequest Request, string RequestId)> CreateRequestAsync(
        IAsyncDocumentSession session,
        OidcApplication app,
        string userId,
        List<string> scopes,
        string redirectUri,
        string? state,
        string? codeChallenge,
        string? codeChallengeMethod,
        string? nonce,
        CancellationToken ct)
    {
        var requestId = OidcRequestReference.GenerateValue();
        var request = new OidcAuthorizationRequest
        {
            Id = OidcRequestReference.DocumentId(requestId),
            ApplicationId = app.Id!,
            Subject = userId,
            RedirectUri = redirectUri,
            Scopes = scopes,
            CodeChallenge = codeChallenge,
            CodeChallengeMethod = codeChallengeMethod,
            Nonce = nonce,
            State = state,
            Status = "pending",
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddMinutes(10),
        };

        await session.StoreAsync(request, ct);

        // Let RavenDB reap the document. Most requests are consumed within seconds and none is
        // of any use after ExpiresAt, so without this the collection would grow with one dead
        // document per sign-in, forever. Security does not rest on the deletion actually having
        // happened — LoadPendingRequestAsync refuses an expired request either way — which is
        // just as well, since the server sweeps on its own schedule.
        session.Advanced.GetMetadataFor(request)[Constants.Documents.Metadata.Expires] = request.ExpiresAt;

        return (request, requestId);
    }

    /// <summary>The external-login outcome code the Authorization package appends on a refused sign-in.</summary>
    internal const string ExternalLoginQueryParameter = "sparkExternalLogin";

    private static IResult RedirectWithError(string redirectUri, string? state, string error, string description)
        => Results.Redirect(RedirectUrl.With(redirectUri,
            ("error", error),
            ("error_description", description),
            ("state", state)));
}
