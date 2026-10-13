using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.IdentityProvider.Endpoints.Oidc;
using MintPlayer.Spark.IdentityProvider.Indexes;
using MintPlayer.Spark.IdentityProvider.Models;
using MintPlayer.Spark.IdentityProvider.Services;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Exceptions;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.IdentityProvider.Endpoints;

/// <summary>
/// The parameters of a token request (RFC 6749 §4.1.3, §4.4.2, §6), as the form carries them. Every
/// member is optional at this level: which ones a grant requires is that grant's own check, and the
/// error it answers with is part of the protocol.
/// </summary>
internal sealed record OidcTokenRequest(
    string? GrantType,
    string? ClientId,
    string? ClientSecret,
    string? Code,
    string? RedirectUri,
    string? CodeVerifier,
    string? RefreshToken,
    string? Scope);

/// <summary>The token endpoint.</summary>
/// <remarks>
/// <para>
/// Generic over the application's user type, which only the application knows: it is closed once,
/// when the routes are mapped, from <c>SparkModuleRegistry.IdentityUserType</c>
/// (<see cref="OidcUserEndpoints"/>), so the user is loaded through a typed
/// <see cref="UserManager{TUser}"/> rather than a reflected one on every request.
/// </para>
/// <para>
/// The body is <c>application/x-www-form-urlencoded</c> (RFC 6749 §3.2), so <see cref="BindRequestAsync"/>
/// reads the form instead of the default JSON, and a bind failure answers in RFC 6749 §5.2's shape
/// rather than as problem details: an OAuth client parses <c>error</c>, nothing else.
/// </para>
/// <para>
/// ⚠️ Deliberately NOT antiforgery-protected: a machine endpoint authenticated by client credentials,
/// never by an ambient cookie (see <see cref="OidcConnectCorsGroup"/>).
/// </para>
/// <para>
/// ⚠️ Not named <c>OidcToken</c> like its siblings: that is the stored token document
/// (<see cref="OidcToken"/>), and a generic <c>OidcToken&lt;TUser&gt;</c> in this namespace or an
/// imported one makes every plain <c>OidcToken</c> beside it ambiguous (CS0104) or wrong (CS0117).
/// </para>
/// </remarks>
[MemberOf<OidcConnectCorsGroup>]
internal sealed partial class OidcTokenEndpoint<TUser> : IPostEndpoint<OidcTokenRequest>
    where TUser : SparkUser, new()
{
    public static string Path => "/token";

    [Inject] private readonly IDocumentStore store;
    [Inject] private readonly OidcTokenGenerator tokenGenerator;
    [Inject] private readonly UserManager<TUser> userManager;
    [Inject] private readonly OidcIssuer oidcIssuer;
    [Inject] private readonly OidcClientAuthenticator clientAuthenticator;
    [Inject] private readonly OidcProofOfPossession proofOfPossession;
    [Inject] private readonly OidcJwe jwe;
    [Inject] private readonly OidcKeyRing signingKeyService;
    [Inject] private readonly OidcAudit audit;
    [Inject] private readonly Mcp.OidcMcpResources mcpResources;
    [Inject] private readonly OidcSurface surface;

    /// <summary>The posted form, kept from <see cref="BindRequestAsync"/>: client authentication reads more of it than the bound record carries.</summary>
    private IFormCollection form = null!;

    /// <summary>
    /// The request being handled, kept from <see cref="BindRequestAsync"/>: the typed handler receives
    /// only the bound request, and the issuer and the <c>no-store</c> headers are per request. The
    /// endpoint is created per request, so this never outlives it.
    /// </summary>
    private HttpContext httpContext = null!;

    protected override async ValueTask<OidcTokenRequest?> BindRequestAsync(HttpContext context)
    {
        httpContext = context;

        if (!context.Request.HasFormContentType)
            throw new EndpointBindingException(StatusCodes.Status400BadRequest, "Content-Type must be application/x-www-form-urlencoded.");

        form = await context.Request.ReadFormAsync(context.RequestAborted);
        return new OidcTokenRequest(
            GrantType: form["grant_type"].FirstOrDefault(),
            ClientId: form["client_id"].FirstOrDefault(),
            ClientSecret: form["client_secret"].FirstOrDefault(),
            Code: form["code"].FirstOrDefault(),
            RedirectUri: form["redirect_uri"].FirstOrDefault(),
            CodeVerifier: form["code_verifier"].FirstOrDefault(),
            RefreshToken: form["refresh_token"].FirstOrDefault(),
            Scope: form["scope"].FirstOrDefault());
    }

    protected override ValueTask<IResult> OnBindFailedAsync(HttpContext context, EndpointBindingException? failure)
        => new(failure is null
            ? Results.Json(new { error = "invalid_request" }, statusCode: StatusCodes.Status400BadRequest)
            : Results.Json(new { error = "invalid_request", error_description = failure.Message }, statusCode: failure.StatusCode));

    // A grant whose flow this provider does not offer (SparkIdentityProviderOptions.Flows) is unsupported, whatever the
    // client is registered for: the discovery documents do not list it either.
    public override Task<IResult> HandleAsync(OidcTokenRequest request, CancellationToken ct)
        => request.GrantType switch
        {
            "authorization_code" when surface.AuthorizationCode => HandleAuthorizationCodeGrant(request, ct),
            "refresh_token" when surface.RefreshToken => HandleRefreshTokenGrant(request, ct),
            "client_credentials" when surface.ClientCredentials => HandleClientCredentialsGrant(request, ct),
            OidcDeviceCodes.GrantType when surface.DeviceCode => HandleDeviceCodeGrant(ct),
            TokenExchangeGrantType when surface.TokenExchange => HandleTokenExchangeGrant(ct),
            _ => Task.FromResult(Results.Json(new { error = "unsupported_grant_type" }, statusCode: 400)),
        };

    private async Task<IResult> HandleAuthorizationCodeGrant(OidcTokenRequest request, CancellationToken ct)
    {
        var code = request.Code;
        var redirectUri = request.RedirectUri;
        var codeVerifier = request.CodeVerifier;

        if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(redirectUri))
        {
            return Results.Json(new { error = "invalid_request", error_description = "Missing required parameters." }, statusCode: 400);
        }

        using var session = store.OpenAsyncSession();

        // Redemption is the point where a single-use credential is spent, so the write that
        // spends it must fail if anyone else spent it first. The point-load fixed replay
        // through a stale index; this fixes replay through simultaneity, where two requests
        // both load a valid code, both check it, and both save.
        session.Advanced.UseOptimisticConcurrency = true;

        // Validate client (D8: basic, post, private_key_jwt, mTLS, or none for a public client)
        var client = await clientAuthenticator.AuthenticateAsync(httpContext, form, session, ct);
        if (!client.Succeeded)
            return client.ToResult(httpContext);
        var app = client.Application!;

        // Check grant type is allowed
        if (!app.AllowedGrantTypes.Contains("authorization_code", StringComparer.OrdinalIgnoreCase))
        {
            return Results.Json(new { error = "unauthorized_client", error_description = "This client is not authorized for authorization_code grant." }, statusCode: 400);
        }

        // Find the authorization code token
        // Point-load, not an index query: index results are eventually consistent, so a code
        // redeemed moments ago could still read back as "valid" and be replayed. Status is
        // therefore checked on the loaded document rather than in the lookup predicate.
        var codeToken = await session.LoadAsync<OidcToken>(OidcTokenReference.DocumentId(code), ct);
        if (codeToken is { Type: "authorization_code", Status: not "valid" })
        {
            // A code presented twice: the first redemption already consumed it. Everything
            // derived from it is now suspect: the family it started is revoked, exactly (RFC 6749
            // §4.1.2), and nothing else of the grant. The bookkeeping after it is best-effort: if a
            // concurrent request is doing the same, either suffices and the caller is refused regardless.
            var startedFamily = OidcRefreshFamilies.DocumentId(codeToken.Id!);
            await RevokeFamilyAsync(startedFamily, OidcRefreshFamilyRevocations.CodeReplay, reused: null, ct);
            await SweepFamilyAsync(session, codeToken, startedFamily, ct);
            await TrySaveAsync(session, ct);

            return Results.Json(new { error = "invalid_grant", error_description = "Invalid or expired authorization code." }, statusCode: 400);
        }

        if (codeToken is not { Type: "authorization_code" })
        {
            return Results.Json(new { error = "invalid_grant", error_description = "Invalid or expired authorization code." }, statusCode: 400);
        }

        // The code must belong to the client redeeming it (RFC 6749 4.1.3). Without this a
        // public client can redeem a confidential client's code without any secret — the
        // redirect_uri check below compares against the code's own stored value, which is the
        // *issuing* client's registered URI and therefore public information, so it provides
        // no client binding of its own.
        if (!string.Equals(codeToken.ApplicationId, app.Id, StringComparison.Ordinal))
        {
            return Results.Json(new { error = "invalid_grant", error_description = "Invalid or expired authorization code." }, statusCode: 400);
        }

        // Validate the code hasn't expired. The same answer as a code that was never issued, replayed or another
        // client's (O15, matrix T-O2): a message of its own told whoever holds a code that it once existed. The
        // refresh grant already answers this way (T-O3).
        if (codeToken.ExpiresAt < DateTime.UtcNow)
        {
            codeToken.Status = "expired";
            await TrySaveAsync(session, ct);
            return Results.Json(new { error = "invalid_grant", error_description = "Invalid or expired authorization code." }, statusCode: 400);
        }

        // Validate redirect_uri matches
        if (!string.Equals(codeToken.RedirectUri, redirectUri, StringComparison.Ordinal))
        {
            return Results.Json(new { error = "invalid_grant", error_description = "redirect_uri mismatch." }, statusCode: 400);
        }

        // Validate PKCE code_verifier
        if (!string.IsNullOrEmpty(codeToken.CodeChallenge))
        {
            if (string.IsNullOrEmpty(codeVerifier))
            {
                return Results.Json(new { error = "invalid_grant", error_description = "PKCE code_verifier is required." }, statusCode: 400);
            }

            // Constant-time, for consistency with the deliberate timing hygiene in
            // VerifyClientSecret. The stored side is the public challenge rather than the
            // secret verifier, so the leak here is slight — but "slight" is a judgement that
            // has to be re-made every time someone reads this line, and FixedTimeEquals costs
            // nothing.
            var computedChallenge = ComputeS256Challenge(codeVerifier);
            if (!FixedTimeEquals(computedChallenge, codeToken.CodeChallenge))
            {
                // Audited (MCP plan M3b): a wrong verifier is what an intercepted code looks like when it is tried.
                await RecordPkceFailureAsync(codeToken, ct);
                return Results.Json(new { error = "invalid_grant", error_description = "PKCE verification failed." }, statusCode: 400);
            }
        }

        if (ResourceMismatch(codeToken) is { } invalidTarget)
            return invalidTarget;

        // Mark code as redeemed (single-use)
        codeToken.Status = "redeemed";
        codeToken.RedeemedAt = DateTime.UtcNow;

        // Load user
        var user = await userManager.FindByIdAsync(codeToken.Subject);
        if (user == null)
        {
            return Results.Json(new { error = "invalid_grant", error_description = "User not found." }, statusCode: 400);
        }

        // Load scope definitions from DB
        var grantedScopes = await LoadScopesAsync(session, codeToken, codeToken.Scopes, ct);
        var grantedScopeNames = GrantedNames(grantedScopes);
        if (IsMcp(codeToken) && !grantedScopes.Any(s => s.Audience is not null))
            return McpResourceUnavailable();

        var issuer = oidcIssuer.Resolve(httpContext.Request);

        // Generate tokens
        // D8: DPoP or certificate binding, decided before anything is minted.
        var possession = await proofOfPossession.BindAsync(httpContext, app, client.Certificate, ct);
        if (!possession.Succeeded)
            return Results.Json(new { error = possession.Error, error_description = possession.ErrorDescription }, statusCode: 400);

        var (accessToken, accessTokenJti) = tokenGenerator.GenerateAccessToken(user, app, issuer, grantedScopes, app.AccessTokenLifetimeMinutes,
            codeToken.Properties, possession.Confirmation);
        // An id_token asserts an authentication event, which is what the openid scope requests.
        // Issuing one regardless meant a client that only asked for API access still received a
        // signed identity assertion it never sought.
        var idToken = GrantsOpenId(codeToken.Scopes)
            ? tokenGenerator.GenerateIdToken(user, app, issuer, grantedScopes, codeToken.Nonce, app.EffectiveIdTokenLifetimeMinutes(),
                accessToken: accessToken, authTime: codeToken.AuthTime, properties: codeToken.Properties)
            : null;
        if (idToken is not null)
        {
            // D8: sealed for a client that registered id_token encryption; never sent in the clear instead.
            idToken = await jwe.EncryptAsync(idToken, app, app.IdTokenEncryptedResponseAlg, app.IdTokenEncryptedResponseEnc, ct);
            if (idToken is null)
                return Results.Json(new { error = "invalid_client", error_description = "The client asked for encrypted id_tokens and has no usable encryption key." }, statusCode: 400);
        }

        // A refresh token is a long-lived credential and must be asked for. This used to be
        // minted unconditionally, so every browser client silently received a 14-day credential
        // it never requested and could not decline — the widest-reaching thing this endpoint
        // handed out, given away by default.
        //
        // An agent's request for the MCP resource is the exception (MCP PRD D5, gap G8): an agent is a native client
        // that must keep working after the access token expires, and its grant, not a remembered consent, is what the
        // user manages; Gemini and Codex do not always ask for offline_access.
        var mcpToken = IsMcp(codeToken);
        var issueRefreshToken = AllowsRefreshTokens(app)
            && (mcpToken || codeToken.Scopes.Contains("offline_access", StringComparer.OrdinalIgnoreCase));
        var refreshTokenValue = issueRefreshToken ? tokenGenerator.GenerateRefreshToken() : null;

        // Store access token
        var accessTokenDoc = new OidcToken
        {
            // Keyed by jti so the token can be looked up, and therefore revoked.
            Id = OidcTokenReference.DocumentId(accessTokenJti),
            ApplicationId = app.Id!,
            AuthorizationId = codeToken.AuthorizationId,
            Subject = codeToken.Subject,
            SessionId = codeToken.SessionId,
            Type = OidcTokenTypes.AccessToken,
            Scopes = grantedScopeNames,
            Status = "valid",
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddMinutes(app.AccessTokenLifetimeMinutes),
        };

        await session.StoreExpiringAsync(accessTokenDoc, ct);

        if (refreshTokenValue != null)
        {
            var refreshTokenDoc = new OidcToken
            {
                ApplicationId = app.Id!,
                AuthorizationId = codeToken.AuthorizationId,
                Subject = codeToken.Subject,
                // An agent's refresh token is not tied to the browser session it was consented in: signing out of the
                // website must not sign the agent out. The user revokes it under Connected applications (D12, D14).
                SessionId = mcpToken ? null : codeToken.SessionId,
                Id = OidcTokenReference.DocumentId(refreshTokenValue),
                Type = OidcTokenTypes.RefreshToken,
                Scopes = grantedScopeNames,
                Status = "valid",
                CreatedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddDays(app.RefreshTokenLifetimeDays),
                AuthTime = codeToken.AuthTime,
                // The sign-in context travels with the refresh token, and so does a DPoP key: a
                // public client's refresh token is bound to it (RFC 9449 §5).
                Properties = WithBinding(codeToken.Properties, possession),
            };
            // The refresh token starts a family, which the access token issued beside it joins: a reuse, or this code
            // presented again, revokes both through the family's document (OidcRefreshFamily).
            await OidcRefreshFamilies.StartAsync(session, codeToken, refreshTokenDoc, accessTokenDoc, ct);
            await session.StoreExpiringAsync(refreshTokenDoc, ct);
        }

        // Marking the code redeemed and issuing the tokens are one batch, so losing the race
        // writes nothing at all. The winner holds the tokens; this request gets the same answer
        // a later replay would get, and the same teardown: when the winner redeemed the code,
        // this was its second spend, and the family the winner started is revoked.
        if (!await TrySaveAsync(session, ct))
        {
            if (await SpentMeanwhileAsync(codeToken.Id!, ct))
                await RevokeFamilyAsync(OidcRefreshFamilies.DocumentId(codeToken.Id!), OidcRefreshFamilyRevocations.CodeReplay, reused: null, ct);
            return Results.Json(new { error = "invalid_grant", error_description = "Invalid or expired authorization code." }, statusCode: 400);
        }

        await OidcGrantUsage.TouchAsync(store, codeToken.AuthorizationId, ct);
        if (mcpToken)
            await RecordAgentTokenAsync(app, codeToken.Subject, "authorization_code", ct);

        httpContext.Response.Headers.CacheControl = "no-store";
        httpContext.Response.Headers.Pragma = "no-cache";

        // Built as a dictionary so an unissued refresh token is absent rather than present-and-
        // null: RFC 6749 §5.1 makes refresh_token optional, and a client testing for the key
        // should not have to distinguish "no refresh token" from "a null one".
        var response = new Dictionary<string, object>
        {
            ["access_token"] = accessToken,
            ["token_type"] = possession.TokenType,
            ["expires_in"] = app.AccessTokenLifetimeMinutes * 60,
        };

        if (idToken != null)
            response["id_token"] = idToken;

        if (refreshTokenValue != null)
            response["refresh_token"] = refreshTokenValue;

        AnnounceScope(response, codeToken.Scopes, grantedScopeNames);

        return Results.Json(response);
    }

    /// <summary>
    /// The scopes a token is actually issued with: those the request carried that resolve to a
    /// defined, enabled scope (<see cref="OidcScopeCatalog"/>).
    /// <para>
    /// This is what must be recorded, because the JWT is minted from it. Storing the requested
    /// list instead made the token document over-report — and introspection reads the document, so
    /// a resource server asking what a token may do was told about scopes the token does not
    /// carry, which is the dangerous direction to be wrong in. It also made disabling a scope a
    /// half-measure: the JWT dropped it at the next issuance while introspection kept vouching for
    /// it, for as long as the refresh token lived.
    /// </para>
    /// </summary>
    /// <summary>The token's recorded context, plus the DPoP key it is bound to.</summary>
    private static Dictionary<string, string> WithBinding(IReadOnlyDictionary<string, string> properties, OidcPossession possession)
    {
        var copy = new Dictionary<string, string>(properties);
        if (possession.Confirmation?.GetValueOrDefault("jkt") is string jkt)
            copy["jkt"] = jkt;
        return copy;
    }

    private static List<string> GrantedNames(List<OidcScopeDefinition> granted)
        => [.. granted.Select(s => s.Name)];

    /// <summary>
    /// Echoes <c>scope</c> when less was granted than asked for. RFC 6749 §5.1 requires it, and
    /// the reason is this case exactly: narrowing is otherwise invisible to the client, which goes
    /// on to call an API it believes it has access to.
    /// </summary>
    private static void AnnounceScope(Dictionary<string, object> response, List<string> requested, List<string> granted)
    {
        if (granted.Count != requested.Count)
            response["scope"] = string.Join(' ', granted);
    }

    /// <summary>
    /// Whether this client may hold refresh tokens at all. Checked both when one is asked for
    /// and when one would be handed out alongside an authorization code.
    /// </summary>
    /// <summary>Whether the code or refresh token belongs to an agent's request for the MCP resource (MCP PRD D5).</summary>
    private static bool IsMcp(OidcToken token) => token.Properties.ContainsKey(Mcp.OidcAgentClients.RequestProperty);

    /// <summary>
    /// RFC 8707 §2.2 at the token endpoint, for MCP tokens: a <c>resource</c> the client names here must be one the
    /// authorization was for. Codex sends it on every request; a different one is <c>invalid_target</c>, never a
    /// token silently addressed elsewhere (MCP PRD D5, gap G4).
    /// </summary>
    private IResult? ResourceMismatch(OidcToken token)
    {
        var named = form["resource"].Where(r => !string.IsNullOrEmpty(r)).Select(r => r!).ToList();
        if (named.Count == 0 || !IsMcp(token))
            return null;

        var recorded = (token.Properties.GetValueOrDefault("resource") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return named.All(r => recorded.Contains(r, StringComparer.Ordinal))
            ? null
            : Results.Json(new { error = "invalid_target", error_description = "This grant is not for that resource." }, statusCode: 400);
    }

    /// <summary>
    /// The definitions of <paramref name="names"/> for the token chain <paramref name="token"/> belongs to. An agent's
    /// chain resolves its MCP scopes in the resource of the endpoint it was authorized for (its recorded
    /// <c>resource</c>), so a token for another application's endpoint keeps that audience; an endpoint no longer
    /// configured leaves them undefined, and the caller answers <see cref="McpResourceUnavailable"/>.
    /// </summary>
    private Task<List<OidcScopeDefinition>> LoadScopesAsync(IAsyncDocumentSession session, OidcToken token, List<string> names, CancellationToken ct)
    {
        if (!IsMcp(token))
            return Token.LoadScopesAsync(session, names, ct);
        return mcpResources.Of(token) is { } endpoint
            ? OidcScopeCatalog.LoadAsync(session, names, endpoint.ResourceId, ct)
            : OidcScopeCatalog.LoadAsync(session, names.Where(n => !Mcp.OidcMcpResources.AsksForMcp([n])), ct);
    }

    /// <summary>The MCP resource or its scopes were disabled after the grant: no token, rather than one addressed elsewhere.</summary>
    private static IResult McpResourceUnavailable()
        => Results.Json(new { error = "invalid_scope", error_description = "The MCP resource is not available." }, statusCode: 400);

    private async Task RecordPkceFailureAsync(OidcToken codeToken, CancellationToken ct)
    {
        using var auditSession = store.OpenAsyncSession();
        await audit.RecordAsync(auditSession, OidcAuditKinds.PkceFailed, actorId: null, codeToken.ApplicationId, codeToken.Subject,
            httpContext.Connection.RemoteIpAddress?.ToString(), ct: ct);
        await auditSession.SaveChangesAsync(ct);
    }

    /// <summary>The audit event of an agent's token issue, and the client's last use (an unused registration is pruned, M3b).</summary>
    private async Task RecordAgentTokenAsync(OidcApplication app, string? subject, string grantType, CancellationToken ct)
    {
        using var bookkeeping = store.OpenAsyncSession();
        await audit.RecordAsync(bookkeeping, OidcAuditKinds.TokenIssued, actorId: subject, app.Id, subject,
            httpContext.Connection.RemoteIpAddress?.ToString(), new Dictionary<string, string> { ["grantType"] = grantType }, ct);
        if (Mcp.OidcAgentClients.IsAgent(app) && (app.LastUsedAt is null || app.LastUsedAt < DateTime.UtcNow.AddHours(-1)))
            bookkeeping.Advanced.Patch<OidcApplication, DateTime?>(app.Id!, a => a.LastUsedAt, DateTime.UtcNow);
        await bookkeeping.SaveChangesAsync(ct);
    }

    /// <summary>Whether <paramref name="app"/> gets refresh tokens: this provider offers the flow, and the client is registered for it.</summary>
    private bool AllowsRefreshTokens(OidcApplication app)
        => surface.RefreshToken && app.AllowedGrantTypes.Contains("refresh_token", StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether an authentication assertion was actually asked for.</summary>
    private static bool GrantsOpenId(List<string> scopes)
        => scopes.Contains("openid", StringComparer.OrdinalIgnoreCase);


    private async Task<IResult> HandleRefreshTokenGrant(OidcTokenRequest request, CancellationToken ct)
    {
        var refreshToken = request.RefreshToken;

        if (string.IsNullOrEmpty(refreshToken))
        {
            return Results.Json(new { error = "invalid_request" }, statusCode: 400);
        }

        using var session = store.OpenAsyncSession();

        // Rotation spends the presented token, so it races exactly as code redemption does.
        session.Advanced.UseOptimisticConcurrency = true;

        var client = await clientAuthenticator.AuthenticateAsync(httpContext, form, session, ct);
        if (!client.Succeeded)
            return client.ToResult(httpContext);
        var app = client.Application!;

        // The other two grants have always checked this; this one did not, so a client never
        // registered for refresh could still rotate one indefinitely.
        if (!AllowsRefreshTokens(app))
        {
            return Results.Json(new { error = "unauthorized_client", error_description = "This client is not authorized for refresh_token grant." }, statusCode: 400);
        }


        // Find refresh token
        // Point-load for the same reason as the authorization-code path above, and so is its family,
        // which decides whether the token is the one to honour (OidcRefreshFamily).
        var refreshTokenDoc = await session.LoadAsync<OidcToken>(OidcTokenReference.DocumentId(refreshToken), ct);
        if (refreshTokenDoc is not { Type: "refresh_token" })
            refreshTokenDoc = null;
        var family = refreshTokenDoc is null ? null : await OidcRefreshFamilies.LoadAsync(session, refreshTokenDoc, ct);

        // Only a rotated token counts as reuse. One revoked by logout, a withdrawal, the disable cascade or
        // RevokeGrant is simply refused below; treating it as theft would write a false audit event.
        if (refreshTokenDoc is not null && OidcRefreshFamilies.IsReuse(refreshTokenDoc, family))
        {
            // Reuse of an already-rotated refresh token. Per RFC 6819 §5.2.2.3 this is
            // treated as theft, and RFC 9700 §4.14.2 has the active refresh token revoked: the
            // family goes, exactly, whether or not an index has seen its active token, and nothing
            // else of the grant (another machine's family is not implicated). Bookkeeping after it
            // is best-effort, as on the code grant.
            await RevokeFamilyAsync(refreshTokenDoc.FamilyId, OidcRefreshFamilyRevocations.RefreshTokenReuse, reused: refreshTokenDoc, ct);
            await SweepFamilyAsync(session, refreshTokenDoc, refreshTokenDoc.FamilyId, ct);
            await TrySaveAsync(session, ct);

            return Results.Json(new { error = "invalid_grant", error_description = "Invalid or expired refresh token." }, statusCode: 400);
        }

        // Client binding, as on the code grant: without it any client may present another's
        // refresh token and receive a token carrying the original's subject and scopes. A token
        // that is not its family's active one (revoked, or its family revoked or gone; every
        // refresh token is issued into a family) is refused the same way.
        if (refreshTokenDoc == null
            || !OidcRefreshFamilies.IsActive(refreshTokenDoc, family)
            || refreshTokenDoc.ExpiresAt < DateTime.UtcNow
            || !string.Equals(refreshTokenDoc.ApplicationId, app.Id, StringComparison.Ordinal))
        {
            return Results.Json(new { error = "invalid_grant", error_description = "Invalid or expired refresh token." }, statusCode: 400);
        }

        // Has the user withdrawn this grant? Checked here rather than inside LoadScopesAsync or
        // GrantedNames: all three grants funnel through those, and client_credentials has no
        // grant document by construction, so a check there would refuse every machine token.
        if (!await OidcGrants.PermitsAsync(session, refreshTokenDoc, ct))
        {
            // A withdrawn grant is not a narrowing — the whole chain goes. Anything still
            // outstanding under it was issued on an authority the user has since taken back.
            await RevokeAuthorizationChainAsync(session, refreshTokenDoc, ct);
            await session.SaveChangesAsync(ct);

            return Results.Json(new { error = "invalid_grant", error_description = "Invalid or expired refresh token." }, statusCode: 400);
        }

        // D8 sessions: logout revokes the session's refresh tokens through an index read, which can
        // miss one minted moments before. The session record itself decides here.
        if (await OidcSessionStore.HasEndedAsync(session, refreshTokenDoc.SessionId, ct))
        {
            refreshTokenDoc.Status = "revoked";
            await TrySaveAsync(session, ct);
            return Results.Json(new { error = "invalid_grant", error_description = "Invalid or expired refresh token." }, statusCode: 400);
        }

        // What the presented token entitles the client to ask for. Never mutated: it is the
        // ceiling the successor must inherit (RFC 6749 §6), it is what the stored record should
        // continue to say this token carried, and it is the baseline a narrowing is announced
        // against. Overwriting it did all three kinds of damage at once.
        if (ResourceMismatch(refreshTokenDoc) is { } invalidTarget)
            return invalidTarget;

        var presentedScopes = refreshTokenDoc.Scopes;

        // A refresh may narrow but never widen: re-intersect against what the client is
        // currently allowed, so removing a scope from the application takes effect on the next
        // refresh rather than persisting for the token's life.
        var permittedScopes = presentedScopes
            .Where(s => app.ScopeNames().Contains(s, StringComparer.OrdinalIgnoreCase))
            .ToList();

        // Load user
        var user = await userManager.FindByIdAsync(refreshTokenDoc.Subject);
        if (user == null)
        {
            return Results.Json(new { error = "invalid_grant" }, statusCode: 400);
        }

        // Load scope definitions from DB
        var grantedScopes = await LoadScopesAsync(session, refreshTokenDoc, permittedScopes, ct);
        var grantedScopeNames = GrantedNames(grantedScopes);
        if (IsMcp(refreshTokenDoc) && !grantedScopes.Any(s => s.Audience is not null))
            return McpResourceUnavailable();

        // Nothing left to grant. Minting anyway produced a signed, subject-bearing, 60-minute
        // JWT with no scopes at all, plus a successor that rotates forever into more of the
        // same — and a resource server that checks signature and `active` but not scope reads
        // the holder as an authenticated user. Refuse instead, and tear the family down: a family
        // that can no longer authorize anything is spent, not merely empty. Only this family: the
        // grant's others may carry scopes that are still available.
        if (grantedScopeNames.Count == 0)
        {
            await RevokeFamilyAsync(refreshTokenDoc.FamilyId, OidcRefreshFamilyRevocations.NoScopeLeft, reused: null, ct);
            await SweepFamilyAsync(session, refreshTokenDoc, refreshTokenDoc.FamilyId, ct);
            await session.SaveChangesAsync(ct);

            return Results.Json(new
            {
                error = "invalid_scope",
                error_description = "No scope on this refresh token is still available to this client.",
            }, statusCode: 400);
        }

        var issuer = oidcIssuer.Resolve(httpContext.Request);

        // Generate new tokens
        // D8: the refresh is bound like the original issuance, and a DPoP-bound refresh token
        // only rotates with a proof from the same key.
        var possession = await proofOfPossession.BindAsync(httpContext, app, client.Certificate, ct);
        if (!possession.Succeeded)
            return Results.Json(new { error = possession.Error, error_description = possession.ErrorDescription }, statusCode: 400);
        if (refreshTokenDoc.Properties.GetValueOrDefault("jkt") is { } boundKey
            && !string.Equals(possession.Confirmation?.GetValueOrDefault("jkt") as string, boundKey, StringComparison.Ordinal))
            return Results.Json(new { error = "invalid_dpop_proof", error_description = "This refresh token is bound to another key." }, statusCode: 400);

        var (newAccessToken, newAccessTokenJti) = tokenGenerator.GenerateAccessToken(user, app, issuer, grantedScopes, app.AccessTokenLifetimeMinutes,
            refreshTokenDoc.Properties, possession.Confirmation);
        var newIdToken = GrantsOpenId(grantedScopeNames)
            ? tokenGenerator.GenerateIdToken(user, app, issuer, grantedScopes, null, app.EffectiveIdTokenLifetimeMinutes(),
                accessToken: newAccessToken, authTime: refreshTokenDoc.AuthTime, properties: refreshTokenDoc.Properties)
            : null;
        if (newIdToken is not null)
        {
            newIdToken = await jwe.EncryptAsync(newIdToken, app, app.IdTokenEncryptedResponseAlg, app.IdTokenEncryptedResponseEnc, ct);
            if (newIdToken is null)
                return Results.Json(new { error = "invalid_client", error_description = "The client asked for encrypted id_tokens and has no usable encryption key." }, statusCode: 400);
        }
        var newRefreshTokenValue = tokenGenerator.GenerateRefreshToken();

        // Revoke old refresh token
        refreshTokenDoc.Status = "redeemed";
        refreshTokenDoc.RedeemedAt = DateTime.UtcNow;

        // Store new tokens
        var newAccessTokenDoc = new OidcToken
        {
            Id = OidcTokenReference.DocumentId(newAccessTokenJti),
            ApplicationId = app.Id!,
            AuthorizationId = refreshTokenDoc.AuthorizationId,
            Subject = refreshTokenDoc.Subject,
            SessionId = refreshTokenDoc.SessionId,
            Type = OidcTokenTypes.AccessToken,
            Scopes = grantedScopeNames,
            Status = "valid",
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddMinutes(app.AccessTokenLifetimeMinutes),
        };

        var newRefreshTokenDoc = new OidcToken
        {
            ApplicationId = app.Id!,
            AuthorizationId = refreshTokenDoc.AuthorizationId,
            Subject = refreshTokenDoc.Subject,
            SessionId = refreshTokenDoc.SessionId,
            Id = OidcTokenReference.DocumentId(newRefreshTokenValue),
            Type = OidcTokenTypes.RefreshToken,
            // RFC 6749 §6: "If a new refresh token is issued, the refresh token scope MUST be
            // identical to that of the refresh token included by the client in the request."
            // Writing the narrowed set here was also a one-way ratchet — a scope disabled for an
            // hour was gone from the chain permanently, because re-enabling it could not put back
            // what the successor no longer carried. The refresh token's list is the grant
            // ceiling; every issuance re-intersects, so an entry that is currently unavailable is
            // inert rather than dangerous.
            Scopes = presentedScopes,
            Status = "valid",
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(app.RefreshTokenLifetimeDays),
            // A refresh is not a re-authentication: auth_time stays the original sign-in.
            AuthTime = refreshTokenDoc.AuthTime,
            Properties = WithBinding(refreshTokenDoc.Properties, possession),
        };

        // The successor becomes the family's active refresh token. The family is written with the
        // rotation, so a rotation racing this one, or a revocation of the family, fails the batch.
        OidcRefreshFamilies.Advance(session, family, newRefreshTokenDoc, newAccessTokenDoc);
        await session.StoreExpiringAsync(newAccessTokenDoc, ct);
        await session.StoreExpiringAsync(newRefreshTokenDoc, ct);

        // As on the code grant: rotation and issuance are one batch, so the loser of a
        // simultaneous rotation writes nothing and is answered as a replay, with a replay's
        // teardown: when the winner spent the same token, this was its second spend, and the
        // family goes (RFC 9700 §4.14.2), the winner's successor with it. When the winner
        // revoked the family instead, nothing is left to do.
        if (!await TrySaveAsync(session, ct))
        {
            if (await SpentMeanwhileAsync(refreshTokenDoc.Id!, ct))
                await RevokeFamilyAsync(refreshTokenDoc.FamilyId, OidcRefreshFamilyRevocations.RefreshTokenReuse, reused: refreshTokenDoc, ct);
            return Results.Json(new { error = "invalid_grant", error_description = "Invalid or expired refresh token." }, statusCode: 400);
        }

        await OidcGrantUsage.TouchAsync(store, refreshTokenDoc.AuthorizationId, ct);
        if (IsMcp(refreshTokenDoc))
            await RecordAgentTokenAsync(app, refreshTokenDoc.Subject, "refresh_token", ct);

        httpContext.Response.Headers.CacheControl = "no-store";
        httpContext.Response.Headers.Pragma = "no-cache";

        var response = new Dictionary<string, object>
        {
            ["access_token"] = newAccessToken,
            ["token_type"] = possession.TokenType,
            ["expires_in"] = app.AccessTokenLifetimeMinutes * 60,
            ["refresh_token"] = newRefreshTokenValue,
        };

        if (newIdToken != null)
            response["id_token"] = newIdToken;

        // A refresh token outlives the configuration it was minted under. Disabling a scope, or
        // removing one from the client, is meant to take capability away — this is where the
        // client finds out it has. Measured against the scopes it *presented*: comparing against
        // the narrowed list (which the old code had already overwritten in place) made the counts
        // equal and the announcement silent for exactly the cases it existed to report.
        AnnounceScope(response, presentedScopes, grantedScopeNames);

        return Results.Json(response);
    }

    private async Task<IResult> HandleClientCredentialsGrant(OidcTokenRequest request, CancellationToken ct)
    {
        var scope = request.Scope;

        using var session = store.OpenAsyncSession();

        var client = await clientAuthenticator.AuthenticateAsync(httpContext, form, session, ct);
        if (!client.Succeeded)
            return client.ToResult(httpContext);
        var app = client.Application!;

        // A machine client authenticates itself; a public client has nothing to authenticate with.
        if (client.Method == OidcClientAuthMethods.None)
            return Results.Json(new { error = "unauthorized_client", error_description = "A public client cannot use client_credentials." }, statusCode: 400);

        // Check grant type is allowed
        if (!app.AllowedGrantTypes.Contains("client_credentials", StringComparer.OrdinalIgnoreCase))
        {
            return Results.Json(new { error = "unauthorized_client", error_description = "This client is not authorized for client_credentials grant." }, statusCode: 400);
        }

        // Parse and validate requested scopes
        var requestedScopes = (scope ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        foreach (var s in requestedScopes)
        {
            if (!app.ScopeNames().Contains(s, StringComparer.OrdinalIgnoreCase))
            {
                return Results.Json(new { error = "invalid_scope", error_description = $"Scope '{s}' is not allowed for this client." }, statusCode: 400);
            }
        }

        // Requiring the caller to name what it wants, rather than defaulting to everything the
        // client may ever hold. The previous default handed a machine token the client's full
        // authority — api.admin included — to a caller that asked for nothing at all, which is
        // least privilege violated by omission and invisible at the call site.
        if (requestedScopes.Count == 0)
        {
            return Results.Json(new { error = "invalid_scope", error_description = "scope is required; name the scopes this token needs." }, statusCode: 400);
        }

        // Load scope definitions from DB
        // D4: a machine client has no team to stand in for; a scope pending its owner's approval, or
        // rejected, is not issued to it.
        requestedScopes = OidcApplicationAccess.AvailableScopes(app, userId: "", requestedScopes);
        var grantedScopes = await Token.LoadScopesAsync(session, requestedScopes, ct);
        var grantedScopeNames = GrantedNames(grantedScopes);

        // Refused rather than narrowed. There is no user and no consent step here: the caller
        // named exactly what it needs, so silently issuing a token for less produces a machine
        // client that fails later, at a call site far from the cause. The client's AllowedScopes
        // check above does not cover this — a scope can be listed on the client and yet be
        // undefined or disabled provider-side, which is the mismatch that let a granted scope
        // vanish from the token in the first place.
        if (grantedScopeNames.Count != requestedScopes.Count)
        {
            var missing = requestedScopes.Except(grantedScopeNames, StringComparer.Ordinal);
            return Results.Json(new
            {
                error = "invalid_scope",
                error_description = $"No enabled scope is defined for: {string.Join(", ", missing)}.",
            }, statusCode: 400);
        }

        var issuer = oidcIssuer.Resolve(httpContext.Request);

        // Generate access token only (no user, no ID token, no refresh token)
        var possession = await proofOfPossession.BindAsync(httpContext, app, client.Certificate, ct);
        if (!possession.Succeeded)
            return Results.Json(new { error = possession.Error, error_description = possession.ErrorDescription }, statusCode: 400);

        var (accessToken, accessTokenJti) = tokenGenerator.GenerateAccessToken(null, app, issuer, grantedScopes, app.AccessTokenLifetimeMinutes,
            confirmation: possession.Confirmation);

        // Store access token
        var accessTokenDoc = new OidcToken
        {
            // Without this key a machine token was unrevocable outright: nothing tied the JWT
            // to a record, so there was no handle to revoke.
            Id = OidcTokenReference.DocumentId(accessTokenJti),
            ApplicationId = app.Id!,
            Subject = $"client:{app.ClientId}",
            Type = OidcTokenTypes.AccessToken,
            Scopes = grantedScopeNames,
            Status = "valid",
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddMinutes(app.AccessTokenLifetimeMinutes),
        };

        await session.StoreExpiringAsync(accessTokenDoc, ct);
        await session.SaveChangesAsync(ct);

        httpContext.Response.Headers.CacheControl = "no-store";
        httpContext.Response.Headers.Pragma = "no-cache";

        return Results.Json(new
        {
            access_token = accessToken,
            token_type = possession.TokenType,
            expires_in = app.AccessTokenLifetimeMinutes * 60,
            scope = string.Join(' ', grantedScopeNames),
        });
    }

    /// <summary>
    /// Marks every token issued under the withdrawn authorization of <paramref name="compromised"/>
    /// revoked: bookkeeping for a grant the user took back, whose tokens every use already refuses
    /// by loading the grant (<see cref="OidcGrants.PermitsAsync"/>).
    /// <para>
    /// The sweep is by <c>AuthorizationId</c> and therefore rides an eventually-consistent index:
    /// a token issued moments earlier may be missed, which is harmless here because the grant
    /// decides. A theft signal never sweeps the grant (<see cref="SweepFamilyAsync"/>): one user
    /// runs one agent client on several machines, each a family of the same grant, and only the
    /// family the signal came from is implicated.
    /// </para>
    /// </summary>
    private static async Task RevokeAuthorizationChainAsync(
        IAsyncDocumentSession session, OidcToken compromised, CancellationToken ct)
    {
        compromised.Status = "revoked";

        if (string.IsNullOrEmpty(compromised.AuthorizationId))
            return;

        var siblings = await session
            .Query<OidcToken>()
            .Where(t => t.AuthorizationId == compromised.AuthorizationId && t.Status == "valid")
            .ToListAsync(ct);

        foreach (var sibling in siblings)
            sibling.Status = "revoked";
    }

    /// <summary>
    /// The bookkeeping after a family's revocation (<see cref="RevokeFamilyAsync"/>): the presented
    /// credential is marked revoked, and so are the family's own tokens the grant's index already
    /// holds. Best-effort, and never the decision: every use of a token loads its family. It reads
    /// the index the grant sweep uses and keeps only <paramref name="familyId"/>'s tokens, so it
    /// needs no index of its own and never touches another family of the grant.
    /// </summary>
    private static async Task SweepFamilyAsync(
        IAsyncDocumentSession session, OidcToken presented, string? familyId, CancellationToken ct)
    {
        presented.Status = "revoked";

        if (string.IsNullOrEmpty(familyId) || string.IsNullOrEmpty(presented.AuthorizationId))
            return;

        var tokens = await session
            .Query<OidcToken>()
            .Where(t => t.AuthorizationId == presented.AuthorizationId && t.Status == "valid")
            .ToListAsync(ct);

        foreach (var token in tokens.Where(t => t.FamilyId == familyId))
            token.Status = "revoked";
    }

    /// <summary>
    /// Revokes the refresh-token family of a spent credential: the decision a theft signal makes
    /// (<c>docs/spark_mcp_PRD.md</c> decision log, "Refresh-token families exact").
    /// <para>
    /// Committed in a batch of its own, which checks no change vector: neither a rotation that
    /// committed after the family was read nor a lost race of the sweep's bookkeeping can undo
    /// it, and a rotation that read the family before it fails on its own optimistic
    /// concurrency. A refresh-token reuse is audited in the same batch (D9: the one token event
    /// that signals theft), so the event lands even when the bookkeeping loses a race.
    /// </para>
    /// </summary>
    /// <param name="reused">The presented refresh token when the signal is its reuse; null for a code spent twice.</param>
    private async Task RevokeFamilyAsync(string? familyId, string reason, OidcToken? reused, CancellationToken ct)
    {
        using var revocation = store.OpenAsyncSession();
        revocation.Advanced.OptimisticConcurrencyMode = OptimisticConcurrencyMode.None;

        var revoked = await OidcRefreshFamilies.RevokeAsync(revocation, familyId, reason, ct);
        if (reused is not null)
        {
            await audit.RecordAsync(revocation, OidcAuditKinds.RefreshTokenReuse, actorId: null,
                reused.ApplicationId, reused.Subject,
                httpContext.Connection.RemoteIpAddress?.ToString(), ct: ct);
        }

        if (revoked || reused is not null)
            await revocation.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Whether the single-use credential recorded at <paramref name="tokenId"/> was spent by
    /// another request, read afresh after this one lost the race for it.
    /// </summary>
    private async Task<bool> SpentMeanwhileAsync(string tokenId, CancellationToken ct)
    {
        using var session = store.OpenAsyncSession();
        return await session.LoadAsync<OidcToken>(tokenId, ct) is { Status: "redeemed" };
    }

    /// <summary>
    /// Saves, reporting whether this request won the race rather than throwing.
    /// <para>
    /// Losing is not an error condition here — it is the expected outcome when a single-use
    /// credential is presented twice at once, and it means nothing was written, because
    /// RavenDB applies a session's changes as one batch. Callers that were spending a
    /// credential must refuse; callers doing best-effort bookkeeping can ignore the result,
    /// since they are already returning an error.
    /// </para>
    /// </summary>
    private static async Task<bool> TrySaveAsync(IAsyncDocumentSession session, CancellationToken ct)
    {
        try
        {
            await session.SaveChangesAsync(ct);
            return true;
        }
        catch (ConcurrencyException)
        {
            return false;
        }
    }

    private static bool FixedTimeEquals(string a, string b)
        => CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    private static string ComputeS256Challenge(string codeVerifier)
    {
        var bytes = Encoding.ASCII.GetBytes(codeVerifier);
        var hash = SHA256.HashData(bytes);
        return Convert.ToBase64String(hash).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}

/// <summary>
/// The token rules the other protocol endpoints share: <c>/connect/authorize</c> resolves scopes the
/// same way, and <c>/connect/introspect</c> and <c>/connect/revoke</c> authenticate clients the same way.
/// </summary>
/// <remarks>
/// Kept outside <see cref="OidcTokenEndpoint{TUser}"/> because none of it depends on the user type, and a
/// caller should not have to close a generic endpoint to reach a static rule.
/// </remarks>
internal static class Token
{
    internal static Task<List<OidcScopeDefinition>> LoadScopesAsync(IAsyncDocumentSession session, List<string> scopeNames, CancellationToken ct)
        => OidcScopeCatalog.LoadAsync(session, scopeNames, ct);

    internal static bool VerifyClientSecret(string secret, List<ClientSecret> secrets)
    {
        if (secrets.Count == 0) return false;

        var now = DateTime.UtcNow;

        // Every unexpired secret is checked even once one matches: short-circuiting on the
        // first hit would leak, through timing, which of a rotating set was presented.
        var matched = false;
        foreach (var candidate in secrets)
        {
            if (candidate.ExpiresAt != null && candidate.ExpiresAt <= now)
                continue;

            if (ClientSecretHasher.Verify(secret, candidate.Hash))
                matched = true;
        }

        return matched;
    }
}
