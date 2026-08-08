using MintPlayer.Spark.IdentityProvider.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.IdentityProvider.Configuration;
using MintPlayer.Spark.IdentityProvider.Indexes;
using MintPlayer.Spark.IdentityProvider.Models;
using Raven.Client;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.IdentityProvider.Endpoints;

internal static class Authorize
{
    public static async Task Handle(HttpContext context)
    {
        var ct = context.RequestAborted;
        var query = context.Request.Query;

        var clientId = query["client_id"].FirstOrDefault();
        var redirectUri = query["redirect_uri"].FirstOrDefault();
        var responseType = query["response_type"].FirstOrDefault();
        var scope = query["scope"].FirstOrDefault();
        var state = query["state"].FirstOrDefault();
        var codeChallenge = query["code_challenge"].FirstOrDefault();
        var codeChallengeMethod = query["code_challenge_method"].FirstOrDefault();
        var nonce = query["nonce"].FirstOrDefault();

        // Validate required parameters
        if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(redirectUri) ||
            string.IsNullOrEmpty(responseType) || string.IsNullOrEmpty(scope))
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsJsonAsync(new { error = "invalid_request", error_description = "Missing required parameters." });
            return;
        }

        if (responseType != "code")
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsJsonAsync(new { error = "unsupported_response_type", error_description = "Only 'code' response type is supported." });
            return;
        }

        // Lookup client application
        var store = context.RequestServices.GetRequiredService<IDocumentStore>();
        using var session = store.OpenAsyncSession();

        var app = await FindApplicationByClientIdAsync(session, clientId, ct);
        if (app == null || !app.Enabled)
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsJsonAsync(new { error = "invalid_client", error_description = "Unknown or disabled client." });
            return;
        }

        // The client must be registered for this grant. Without it, a client provisioned
        // solely for client_credentials — a machine identity, typically holding broader
        // application claims than any user — could still be driven through the interactive
        // flow.
        if (!app.AllowedGrantTypes.Contains("authorization_code", StringComparer.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsJsonAsync(new { error = "unauthorized_client", error_description = "This client is not authorized for authorization_code grant." });
            return;
        }

        // Validate redirect URI
        if (!app.RedirectUris.Contains(redirectUri, StringComparer.Ordinal))
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsJsonAsync(new { error = "invalid_request", error_description = "Invalid redirect_uri." });
            return;
        }

        // Validate PKCE
        if (app.RequirePkce && string.IsNullOrEmpty(codeChallenge))
        {
            RedirectWithError(context, redirectUri, state, "invalid_request", "PKCE code_challenge is required.");
            return;
        }

        if (!string.IsNullOrEmpty(codeChallenge) && codeChallengeMethod != "S256")
        {
            RedirectWithError(context, redirectUri, state, "invalid_request", "Only S256 code_challenge_method is supported.");
            return;
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
                RedirectWithError(context, redirectUri, state, "invalid_scope", $"Scope '{s}' is not allowed for this client.");
                return;
            }

            if (!definedScopes.Any(d => string.Equals(d.Name, s, StringComparison.OrdinalIgnoreCase)))
            {
                RedirectWithError(context, redirectUri, state, "invalid_scope", $"Scope '{s}' is not available.");
                return;
            }
        }

        // Check user authentication
        var userId = await context.GetInteractiveUserIdAsync();
        if (string.IsNullOrEmpty(userId))
        {
            // User not authenticated — redirect to MVC login page
            var currentUrl = context.Request.QueryString.Value;
            var loginUrl = $"/connect/login?returnUrl={Uri.EscapeDataString($"/connect/authorize{currentUrl}")}";
            context.Response.Redirect(loginUrl);
            return;
        }

        // Everything above validated the request against the application record. Persist that
        // verdict now and hand the browser nothing but an opaque handle to it, so no later hop
        // has to — or is able to — re-derive it from request input.
        var (request, requestId) = await CreateRequestAsync(
            session, app, userId, requestedScopes, redirectUri, state, codeChallenge, codeChallengeMethod, nonce, ct);

        var options = context.RequestServices.GetRequiredService<SparkIdentityProviderOptions>();

        if (app.ConsentType == "implicit" && options.AutoApproveImplicitConsent)
        {
            request.AuthorizationId = await EnsureAuthorizationAsync(session, app, userId, requestedScopes, ct);
            await GenerateCodeAndRedirectAsync(context, session, request, ct);
            return;
        }

        // Check if user already consented for these scopes
        var existingAuth = await session.LoadAsync<OidcAuthorization>(
            OidcAuthorizationReference.DocumentId(userId, app.Id!), ct);

        if (existingAuth is { Status: "valid" })
        {
            var allScopesCovered = requestedScopes.All(s =>
                existingAuth.GrantedScopes.Contains(s, StringComparer.OrdinalIgnoreCase));

            if (allScopesCovered)
            {
                request.AuthorizationId = existingAuth.Id!;
                await GenerateCodeAndRedirectAsync(context, session, request, ct);
                return;
            }
        }

        await session.SaveChangesAsync(ct);
        context.Response.Redirect($"/connect/consent?request_id={Uri.EscapeDataString(requestId)}");
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

    /// <summary>
    /// Returns the id of the user's valid authorization for this application, widening its
    /// granted scopes to cover <paramref name="scopes"/>, and creating it if there is none.
    /// <para>
    /// Every path that mints a code goes through here, which is what keeps
    /// <see cref="OidcToken.AuthorizationId"/> populated. While it was left empty, both
    /// revocation cascades — the one on the revocation endpoint and the reuse-detection
    /// teardown on the token endpoint — silently swept nothing.
    /// </para>
    /// </summary>
    internal static async Task<string> EnsureAuthorizationAsync(
        IAsyncDocumentSession session,
        OidcApplication app,
        string userId,
        List<string> scopes,
        CancellationToken ct)
    {
        var authorizationId = OidcAuthorizationReference.DocumentId(userId, app.Id!);
        var auth = await session.LoadAsync<OidcAuthorization>(authorizationId, ct);

        if (auth == null)
        {
            auth = new OidcAuthorization
            {
                Id = authorizationId,
                ApplicationId = app.Id!,
                Subject = userId,
                CreatedAt = DateTime.UtcNow,
            };

            await session.StoreAsync(auth, ct);
        }

        // Consenting again reinstates a grant the user previously revoked — that is precisely
        // what they have just asked for. Tokens issued before the revocation stay revoked;
        // only the grant itself comes back.
        auth.Status = "valid";
        auth.RevokedAt = null;

        foreach (var s in scopes)
        {
            if (!auth.GrantedScopes.Contains(s, StringComparer.OrdinalIgnoreCase))
                auth.GrantedScopes.Add(s);
        }

        await session.SaveChangesAsync(ct);
        return authorizationId;
    }

    /// <summary>
    /// Mints an authorization code for an already-validated request and redirects the browser
    /// back to the client. Everything the code carries comes from <paramref name="request"/>,
    /// never from the current HTTP request.
    /// </summary>
    internal static async Task GenerateCodeAndRedirectAsync(
        HttpContext context,
        IAsyncDocumentSession session,
        OidcAuthorizationRequest request,
        CancellationToken ct)
    {
        var code = OidcTokenReference.GenerateValue();

        var token = new OidcToken
        {
            // The id is the hash of the code, so redemption is a strongly-consistent
            // point-load and the code itself is never persisted.
            Id = OidcTokenReference.DocumentId(code),
            ApplicationId = request.ApplicationId,
            AuthorizationId = request.AuthorizationId,
            Subject = request.Subject,
            Type = "authorization_code",
            CodeChallenge = request.CodeChallenge,
            CodeChallengeMethod = request.CodeChallengeMethod,
            RedirectUri = request.RedirectUri,
            Scopes = [.. request.Scopes],
            Status = "valid",
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddMinutes(5), // 5 minute lifetime
            State = request.Nonce, // Store nonce for ID token generation
        };

        // A request mints exactly one code. Re-submitting the consent form, or replaying the
        // handle from browser history, finds a consumed request rather than a second code.
        request.Status = "consumed";

        await session.StoreAsync(token, ct);
        await session.SaveChangesAsync(ct);

        // Redirect back to client with authorization code
        context.Response.Redirect(RedirectUrl.With(request.RedirectUri,
            ("code", code),
            ("state", request.State)));
    }

    /// <summary>
    /// Loads the request behind a <c>request_id</c>, or null if it is unknown, expired,
    /// already used, or belongs to a different signed-in user.
    /// </summary>
    internal static async Task<OidcAuthorizationRequest?> LoadPendingRequestAsync(
        IAsyncDocumentSession session, string requestId, string userId, CancellationToken ct)
    {
        var request = await session.LoadAsync<OidcAuthorizationRequest>(
            OidcRequestReference.DocumentId(requestId), ct);

        if (request is not { Status: "pending" })
            return null;

        if (request.ExpiresAt < DateTime.UtcNow)
            return null;

        // The handle is bound to the user it was issued for: one user must not be able to
        // hand another a link that consents on their behalf.
        if (!string.Equals(request.Subject, userId, StringComparison.Ordinal))
            return null;

        return request;
    }

    internal static async Task<OidcApplication?> FindApplicationByClientIdAsync(
        IAsyncDocumentSession session, string clientId, CancellationToken ct)
    {
        // exact: true because RavenDB compares strings case-insensitively by default, which
        // would make "acmeapp" resolve the application registered as "AcmeApp" — impersonation
        // by casing, on the lookup that decides which client every other check is applied to.
        return await session.Query<OidcApplication, OidcApplications_ByClientId>()
            .Where(a => a.ClientId == clientId, exact: true)
            .FirstOrDefaultAsync(ct);
    }

    private static void RedirectWithError(HttpContext context, string redirectUri, string? state,
        string error, string description)
    {
        context.Response.Redirect(RedirectUrl.With(redirectUri,
            ("error", error),
            ("error_description", description),
            ("state", state)));
    }
}
