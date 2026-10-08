using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using MintPlayer.Spark.IdentityProvider.Configuration;
using MintPlayer.Spark.IdentityProvider.Models;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.IdentityProvider.Services;

/// <summary>
/// Everything <c>/connect/authorize</c> decides (OIDC Core §3.1.2, <c>docs/identity_provider_platform_PRD.md</c>
/// D3, D4, D6, D8), for both its GET and POST forms:
/// <list type="number">
/// <item>resolve the request: by value, a pushed request (PAR) or a request object (JAR);</item>
/// <item>validate the client, redirect URI, response type and mode, PKCE, scopes and resources;</item>
/// <item>decide whether the person signed in may answer it: <c>prompt</c>, <c>max_age</c>,
/// <c>acr_values</c> step-up, <c>id_token_hint</c>, Development mode;</item>
/// <item>consent (remembered, incremental, forced by <c>prompt=consent</c>), then the code, delivered by
/// <c>response_mode</c> with <c>iss</c>.</item>
/// </list>
/// Errors that cannot be trusted to a redirect URI (unknown client, unregistered redirect) are answered
/// here; every other error goes back to the client.
/// </summary>
internal sealed class OidcAuthorizeHandler(
    IDocumentStore store,
    SparkIdentityProviderOptions options,
    OidcIssuer oidcIssuer,
    OidcRequestObjects requestObjects,
    OidcKeyRing signingKeys)
{
    /// <summary>The external-login outcome code the Authorization package appends on a refused sign-in (#490 M6).</summary>
    internal const string ExternalLoginQueryParameter = "sparkExternalLogin";

    /// <summary>Marks a return from a sign-in this endpoint forced (prompt=login, max_age, acr step-up), so it is not forced again.</summary>
    private const string ReauthenticatedMarker = "spark_reauth";

    /// <summary>How far an auth_time may lag behind max_age: the time between the sign-in and the redirect back.</summary>
    private static readonly TimeSpan MaxAgeSkew = TimeSpan.FromSeconds(60);

    public async Task<IResult> HandleAsync(HttpContext context, OidcAuthorizeParameters parameters, CancellationToken ct)
    {
        var issuer = oidcIssuer.Resolve(context.Request);
        using var session = store.OpenAsyncSession();

        // --- 1. Resolve PAR / JAR into plain parameters ------------------------------------------
        var resolved = await requestObjects.ResolveAsync(session, parameters, issuer, ct);
        if (resolved.Error is { } resolveError)
            return Json(resolveError, resolved.ErrorDescription!);
        var p = resolved.Parameters!;

        // --- 2. Validate what can be validated before trusting the redirect URI --------------------
        if (string.IsNullOrEmpty(p.ClientId) || string.IsNullOrEmpty(p.RedirectUri) ||
            string.IsNullOrEmpty(p.ResponseType) || string.IsNullOrEmpty(p.Scope))
            return Json("invalid_request", "Missing required parameters.");

        var app = await OidcAuthorizationFlow.FindApplicationByClientIdAsync(session, p.ClientId, ct);
        if (app == null || !app.Enabled)
            return Json("invalid_client", "Unknown or disabled client.");

        // The client must be registered for this grant. Without it, a client provisioned solely for
        // client_credentials — a machine identity, typically holding broader application claims
        // than any user — could still be driven through the interactive flow.
        if (!app.AllowedGrantTypes.Contains("authorization_code", StringComparer.OrdinalIgnoreCase))
            return Json("unauthorized_client", "This client is not authorized for authorization_code grant.");

        if (!app.RedirectUris.Contains(p.RedirectUri, StringComparer.Ordinal))
            return Json("invalid_request", "Invalid redirect_uri.");

        // From here on errors go back to the client, in the mode it asked for.
        var mode = p.ResponseMode is null or OidcAuthorizationResponse.Query or OidcAuthorizationResponse.FormPost ? p.ResponseMode : null;
        IResult Error(string error, string description) => OidcAuthorizationResponse.Error(p.RedirectUri!, mode, issuer, p.State, error, description);

        if (p.ResponseMode is not null && mode is null)
            return Error("invalid_request", $"response_mode '{p.ResponseMode}' is not supported.");
        if (p.ResponseType != "code")
            return Error("unsupported_response_type", "Only 'code' response type is supported.");

        // D8: a client may be required to push its requests, or to sign them.
        if (app.RequirePushedAuthorizationRequests && !resolved.FromPushedRequest)
            return Error("invalid_request", "This client must use pushed authorization requests.");
        if (app.RequireSignedRequestObject && !resolved.FromRequestObject)
            return Error("invalid_request", "This client must send a signed request object.");

        if (app.RequirePkce && string.IsNullOrEmpty(p.CodeChallenge))
            return Error("invalid_request", "PKCE code_challenge is required.");
        if (!string.IsNullOrEmpty(p.CodeChallenge) && p.CodeChallengeMethod != "S256")
            return Error("invalid_request", "Only S256 code_challenge_method is supported.");

        var prompts = p.Prompts;
        if (prompts.Contains("none") && prompts.Count > 1)
            return Error("invalid_request", "prompt=none cannot be combined with other values.");
        if (prompts.Any(v => v is not ("none" or "login" or "consent" or "select_account")))
            return Error("invalid_request", "Unknown prompt value.");

        // Validate requested scopes against BOTH sources of truth.
        //
        // The application's Scopes say what this client may ask for; the OidcResource documents
        // say what the provider actually defines. Only the first was checked once, while token
        // issuance resolves against the second, so a scope listed on the client but undefined (or
        // disabled) was accepted, consented to, and carried on the code, and then silently vanished
        // from the issued token's `scope` claim. Rejecting here makes the disagreement surface at the
        // point of request instead.
        var requestedScopes = p.Scope.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        var definedScopes = await OidcScopeCatalog.LoadAsync(session, requestedScopes, ct);
        foreach (var s in requestedScopes)
        {
            if (!app.ScopeNames().Contains(s, StringComparer.OrdinalIgnoreCase))
                return Error("invalid_scope", $"Scope '{s}' is not allowed for this client.");
            if (!definedScopes.Any(d => string.Equals(d.Name, s, StringComparison.OrdinalIgnoreCase)))
                return Error("invalid_scope", $"Scope '{s}' is not available.");
        }

        // RFC 8707: each resource names an API whose scopes the request carries. The access token is
        // then addressed to those APIs only.
        foreach (var resource in p.Resources)
        {
            if (!definedScopes.Any(d => d.Audience is { } aud && string.Equals(aud, resource, StringComparison.Ordinal)))
                return Error("invalid_target", $"Resource '{resource}' is not an API this request asks for.");
        }

        if (p.Claims is not null && !IsValidClaimsRequest(p.Claims))
            return Error("invalid_request", "The claims parameter is not valid JSON.");

        // --- 3. Who is answering, and is that good enough -----------------------------------------
        var reauthenticated = context.Request.Query.ContainsKey(ReauthenticatedMarker);
        var signedIn = await OidcInteractiveSession.ReadAsync(context);
        if (signedIn is null)
        {
            if (prompts.Contains("none"))
                return Error("login_required", "The user is not signed in.");
            return RedirectToLogin(p, reauthenticate: false);
        }

        // prompt=login / select_account: one fresh sign-in, then on. There is no account picker; signing
        // in again is how another account is selected.
        if (!reauthenticated && (prompts.Contains("login") || prompts.Contains("select_account")))
            return RedirectToLogin(p, reauthenticate: true);

        var maxAge = p.MaxAgeSeconds ?? app.DefaultMaxAge;
        if (maxAge is { } seconds && (signedIn.AuthTime is null || DateTimeOffset.UtcNow - signedIn.AuthTime > TimeSpan.FromSeconds(seconds) + MaxAgeSkew))
        {
            if (prompts.Contains("none"))
                return Error("login_required", "The sign-in is older than max_age.");
            if (!reauthenticated)
                return RedirectToLogin(p, reauthenticate: true);
        }

        // acr_values: step up once (a fresh sign-in passes two-factor where the account has it); a
        // request that still is not met is answered with what was achieved, as acr_values asks for a
        // preference. An essential acr in the claims parameter is a requirement.
        var requestedAcr = p.AcrValues ?? EssentialAcr(p.Claims);
        if (!OidcAcr.Satisfies(signedIn.Acr, requestedAcr))
        {
            if (!reauthenticated && !prompts.Contains("none"))
                return RedirectToLogin(p, reauthenticate: true);
            if (EssentialAcr(p.Claims) is not null)
                return Error("unmet_authentication_requirements", "The requested authentication level could not be met.");
        }

        // id_token_hint names who the client expects; a different signed-in person is not silently
        // substituted when no interaction is allowed.
        if (p.IdTokenHint is not null)
        {
            var hintedSubject = await OidcIdTokenHint.ResolveSubjectAsync(signingKeys, p.IdTokenHint, issuer);
            if (hintedSubject is not null && hintedSubject != OidcSubjects.For(app, signedIn.UserId) && prompts.Contains("none"))
                return Error("login_required", "A different user is signed in.");
        }

        var userId = signedIn.UserId;

        // D4: an application in Development mode is for its own team only. Refused after sign-in,
        // not before: whether the caller is on the team depends on who they are.
        if (!OidcApplicationAccess.MayAuthorize(app, userId))
            return Error("access_denied", "This application is still in development. Only its team can sign in to it.");

        // D4: scopes awaiting their owner's approval are dropped for everyone outside the team, and
        // rejected ones for everyone. Dropped, not refused: the request still succeeds with what is
        // available, and the token response's `scope` says what was granted (D6).
        requestedScopes = OidcApplicationAccess.AvailableScopes(app, userId, requestedScopes);
        if (requestedScopes.Count == 0)
            return Error("invalid_scope", "None of the requested scopes is available.");

        // --- 4. Consent and the code -------------------------------------------------------------
        // Everything above validated the request against the application record. Persist that
        // verdict now and hand the browser nothing but an opaque handle to it, so no later hop has
        // to, or is able to, re-derive it from request input.
        var (request, requestId) = await CreateRequestAsync(session, app, userId, requestedScopes, p, signedIn, mode, ct);

        // Load the user's standing grant once: both the implicit branch and the skip-consent check
        // below need it, and the implicit branch used to run without consulting it at all.
        var existingAuth = await session.LoadAsync<OidcGrant>(OidcGrantReference.DocumentId(userId, app.Id!), ct);
        var withdrawn = existingAuth is not null && existingAuth.Status != "valid";

        // D6, incremental authorization: the client asks for what it needs now and gets what was
        // granted before as well. Only scopes the application may still request are added.
        if (string.Equals(p.IncludeGrantedScopes, "true", StringComparison.OrdinalIgnoreCase) && existingAuth is { Status: "valid" })
        {
            var previously = OidcApplicationAccess.AvailableScopes(app, userId, existingAuth.GrantedScopes);
            request.Scopes = [.. request.Scopes.Union(previously, StringComparer.OrdinalIgnoreCase)];
            requestedScopes = request.Scopes;
        }

        var forceConsent = prompts.Contains("consent");

        // Auto-approval says "this user already trusts this client", which is exactly the statement
        // a withdrawal retracts. A user who has withdrawn must be asked again, whatever the client's
        // consent type.
        if (app.ConsentType == "implicit" && options.AutoApproveImplicitConsent && !withdrawn && !forceConsent)
        {
            request.AuthorizationId = await OidcAuthorizationFlow.EnsureAuthorizationAsync(session, app, userId, requestedScopes, remember: true, ct);
            return await OidcAuthorizationFlow.IssueCodeResponseAsync(session, request, issuer, ct);
        }

        // D6: the consent screen is skipped only for a consent the user asked to be remembered, and
        // only until it expires (ConsentLifetimeSeconds).
        if (!forceConsent && existingAuth is not null && existingAuth.RemembersConsentAt(DateTime.UtcNow)
            && requestedScopes.All(s => existingAuth.GrantedScopes.Contains(s, StringComparer.OrdinalIgnoreCase)))
        {
            request.AuthorizationId = existingAuth.Id!;
            return await OidcAuthorizationFlow.IssueCodeResponseAsync(session, request, issuer, ct);
        }

        if (prompts.Contains("none"))
            return Error("consent_required", "The user has not consented to this request.");

        await session.SaveChangesAsync(ct);
        return Results.Redirect($"/connect/consent?request_id={Uri.EscapeDataString(requestId)}");
    }

    /// <summary>
    /// Sends the browser to the sign-in page, which comes back to this request. A forced re-authentication
    /// marks the way back, so it is forced once: prompt=login does not loop.
    /// </summary>
    private static IResult RedirectToLogin(OidcAuthorizeParameters p, bool reauthenticate)
    {
        var withoutLoginPrompt = reauthenticate && p.Prompt is not null
            ? p with { Prompt = string.Join(' ', p.Prompts.Where(v => v is not ("login" or "select_account"))) is { Length: > 0 } rest ? rest : null }
            : p;
        // #490 M6: a refused external sign-in that came back here is handed to the sign-in page,
        // which shows it, rather than carried inside the returnUrl where it would be dropped.
        var returnQuery = (withoutLoginPrompt with { ExternalLogin = null })
            .ToQueryString(reauthenticate ? [(ReauthenticatedMarker, "1")] : []);

        var loginUrl = $"/connect/login?returnUrl={Uri.EscapeDataString($"/connect/authorize{returnQuery}")}";
        if (!string.IsNullOrEmpty(p.LoginHint))
            loginUrl = QueryHelpers.AddQueryString(loginUrl, "login_hint", p.LoginHint);
        if (!string.IsNullOrEmpty(p.UiLocales))
            loginUrl = QueryHelpers.AddQueryString(loginUrl, "ui_locales", p.UiLocales);
        if (!string.IsNullOrEmpty(p.ExternalLogin))
            loginUrl = QueryHelpers.AddQueryString(loginUrl, ExternalLoginQueryParameter, p.ExternalLogin);
        return Results.Redirect(loginUrl);
    }

    /// <summary>
    /// Records a validated authorization request and returns it together with the handle the browser
    /// carries. The document is stored but not yet saved: the caller decides whether this request goes
    /// to a consent screen or straight to code issuance.
    /// </summary>
    private static async Task<(OidcToken Request, string RequestId)> CreateRequestAsync(
        IAsyncDocumentSession session, OidcApplication app, string userId, List<string> scopes,
        OidcAuthorizeParameters p, OidcInteractiveSession signedIn, string? responseMode, CancellationToken ct)
    {
        var requestId = OidcRequestReference.GenerateValue();
        var properties = new Dictionary<string, string>
        {
            ["amr"] = string.Join(' ', signedIn.Amr),
            ["acr"] = signedIn.Acr,
        };
        void Put(string key, string? value) { if (!string.IsNullOrEmpty(value)) properties[key] = value; }
        Put("response_mode", responseMode);
        Put("ui_locales", p.UiLocales);
        Put("claims", p.Claims);
        Put("max_age", (p.MaxAgeSeconds ?? app.DefaultMaxAge)?.ToString());
        Put("resource", p.Resources.Count > 0 ? string.Join(' ', p.Resources) : null);
        Put("sid", signedIn.SessionId);

        var request = new OidcToken
        {
            Id = OidcRequestReference.DocumentId(requestId),
            Type = OidcTokenTypes.AuthorizationRequest,
            ApplicationId = app.Id!,
            Subject = userId,
            RedirectUri = p.RedirectUri,
            Scopes = scopes,
            CodeChallenge = p.CodeChallenge,
            CodeChallengeMethod = p.CodeChallengeMethod,
            Nonce = p.Nonce,
            State = p.State,
            Status = "pending",
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddMinutes(10),
            AuthTime = signedIn.AuthTime,
            SessionId = signedIn.SessionId,
            Properties = properties,
        };

        // Let RavenDB reap the document. Most requests are consumed within seconds and none is of
        // any use after ExpiresAt. Security does not rest on the deletion having happened —
        // LoadPendingRequestAsync refuses an expired request either way.
        await session.StoreExpiringAsync(request, ct);
        return (request, requestId);
    }

    /// <summary>The <c>acr</c> values the claims parameter marks essential for the id_token, or null.</summary>
    internal static string? EssentialAcr(string? claims)
    {
        if (claims is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(claims);
            if (doc.RootElement.TryGetProperty("id_token", out var idToken)
                && idToken.TryGetProperty("acr", out var acr)
                && acr.ValueKind == JsonValueKind.Object
                && acr.TryGetProperty("essential", out var essential) && essential.ValueKind == JsonValueKind.True)
            {
                if (acr.TryGetProperty("values", out var values) && values.ValueKind == JsonValueKind.Array)
                    return string.Join(' ', values.EnumerateArray().Select(v => v.GetString()).OfType<string>());
                if (acr.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.String)
                    return value.GetString();
            }
        }
        catch (JsonException) { }
        return null;
    }

    private static bool IsValidClaimsRequest(string claims)
    {
        try
        {
            using var doc = JsonDocument.Parse(claims);
            return doc.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException) { return false; }
    }

    private static IResult Json(string error, string description)
        => Results.Json(new { error, error_description = description }, statusCode: StatusCodes.Status400BadRequest);
}
