using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.IdentityProvider.Indexes;
using MintPlayer.Spark.IdentityProvider.Models;
using MintPlayer.Spark.IdentityProvider.Services;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Exceptions;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.IdentityProvider.Endpoints;

internal static class Token
{
    public static async Task Handle(HttpContext context)
    {
        var ct = context.RequestAborted;

        if (!context.Request.HasFormContentType)
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsJsonAsync(new { error = "invalid_request", error_description = "Content-Type must be application/x-www-form-urlencoded." });
            return;
        }

        var form = await context.Request.ReadFormAsync(ct);
        var grantType = form["grant_type"].FirstOrDefault();

        switch (grantType)
        {
            case "authorization_code":
                await HandleAuthorizationCodeGrant(context, form, ct);
                break;
            case "refresh_token":
                await HandleRefreshTokenGrant(context, form, ct);
                break;
            case "client_credentials":
                await HandleClientCredentialsGrant(context, form, ct);
                break;
            default:
                context.Response.StatusCode = 400;
                await context.Response.WriteAsJsonAsync(new { error = "unsupported_grant_type" });
                break;
        }
    }

    private static async Task HandleAuthorizationCodeGrant(HttpContext context, IFormCollection form, CancellationToken ct)
    {
        var clientId = form["client_id"].FirstOrDefault();
        var clientSecret = form["client_secret"].FirstOrDefault();
        var code = form["code"].FirstOrDefault();
        var redirectUri = form["redirect_uri"].FirstOrDefault();
        var codeVerifier = form["code_verifier"].FirstOrDefault();

        if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(code) || string.IsNullOrEmpty(redirectUri))
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsJsonAsync(new { error = "invalid_request", error_description = "Missing required parameters." });
            return;
        }

        var store = context.RequestServices.GetRequiredService<IDocumentStore>();
        using var session = store.OpenAsyncSession();

        // Redemption is the point where a single-use credential is spent, so the write that
        // spends it must fail if anyone else spent it first. The point-load fixed replay
        // through a stale index; this fixes replay through simultaneity, where two requests
        // both load a valid code, both check it, and both save.
        session.Advanced.UseOptimisticConcurrency = true;

        // Validate client
        var app = await Authorize.FindApplicationByClientIdAsync(session, clientId, ct);
        if (app == null || !app.Enabled)
        {
            context.Response.StatusCode = 401;
            await context.Response.WriteAsJsonAsync(new { error = "invalid_client" });
            return;
        }

        // Check grant type is allowed
        if (!app.AllowedGrantTypes.Contains("authorization_code", StringComparer.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsJsonAsync(new { error = "unauthorized_client", error_description = "This client is not authorized for authorization_code grant." });
            return;
        }

        // Validate client secret for confidential clients
        // Fail closed: only a client explicitly marked public, holding no secrets, skips
        // authentication. Comparing == "confidential" meant a stray case or space silently
        // disabled client authentication altogether.
        if (!(string.Equals(app.ClientType, "public", StringComparison.OrdinalIgnoreCase) && app.Secrets.Count == 0))
        {
            if (string.IsNullOrEmpty(clientSecret) || !VerifyClientSecret(clientSecret, app.Secrets))
            {
                context.Response.StatusCode = 401;
                await context.Response.WriteAsJsonAsync(new { error = "invalid_client", error_description = "Invalid client credentials." });
                return;
            }
        }

        // Find the authorization code token
        // Point-load, not an index query: index results are eventually consistent, so a code
        // redeemed moments ago could still read back as "valid" and be replayed. Status is
        // therefore checked on the loaded document rather than in the lookup predicate.
        var codeToken = await session.LoadAsync<OidcToken>(OidcTokenReference.DocumentId(code), ct);
        if (codeToken is { Type: "authorization_code", Status: not "valid" })
        {
            // A code presented twice: the first redemption already consumed it. Everything
            // derived from it is now suspect, so the whole authorization is torn down.
            // Best-effort: if a concurrent request is tearing down the same chain, either
            // teardown suffices and the caller is refused regardless.
            await RevokeAuthorizationChainAsync(session, codeToken, ct);
            await TrySaveAsync(session, ct);

            context.Response.StatusCode = 400;
            await context.Response.WriteAsJsonAsync(new { error = "invalid_grant", error_description = "Invalid or expired authorization code." });
            return;
        }

        if (codeToken is not { Type: "authorization_code" })
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsJsonAsync(new { error = "invalid_grant", error_description = "Invalid or expired authorization code." });
            return;
        }

        // The code must belong to the client redeeming it (RFC 6749 4.1.3). Without this a
        // public client can redeem a confidential client's code without any secret — the
        // redirect_uri check below compares against the code's own stored value, which is the
        // *issuing* client's registered URI and therefore public information, so it provides
        // no client binding of its own.
        if (!string.Equals(codeToken.ApplicationId, app.Id, StringComparison.Ordinal))
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsJsonAsync(new { error = "invalid_grant", error_description = "Invalid or expired authorization code." });
            return;
        }

        // Validate the code hasn't expired
        if (codeToken.ExpiresAt < DateTime.UtcNow)
        {
            codeToken.Status = "expired";
            await TrySaveAsync(session, ct);
            context.Response.StatusCode = 400;
            await context.Response.WriteAsJsonAsync(new { error = "invalid_grant", error_description = "Authorization code has expired." });
            return;
        }

        // Validate redirect_uri matches
        if (!string.Equals(codeToken.RedirectUri, redirectUri, StringComparison.Ordinal))
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsJsonAsync(new { error = "invalid_grant", error_description = "redirect_uri mismatch." });
            return;
        }

        // Validate PKCE code_verifier
        if (!string.IsNullOrEmpty(codeToken.CodeChallenge))
        {
            if (string.IsNullOrEmpty(codeVerifier))
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsJsonAsync(new { error = "invalid_grant", error_description = "PKCE code_verifier is required." });
                return;
            }

            // Constant-time, for consistency with the deliberate timing hygiene in
            // VerifyClientSecret. The stored side is the public challenge rather than the
            // secret verifier, so the leak here is slight — but "slight" is a judgement that
            // has to be re-made every time someone reads this line, and FixedTimeEquals costs
            // nothing.
            var computedChallenge = ComputeS256Challenge(codeVerifier);
            if (!FixedTimeEquals(computedChallenge, codeToken.CodeChallenge))
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsJsonAsync(new { error = "invalid_grant", error_description = "PKCE verification failed." });
                return;
            }
        }

        // Mark code as redeemed (single-use)
        codeToken.Status = "redeemed";
        codeToken.RedeemedAt = DateTime.UtcNow;

        // Load user
        var user = await LoadUserAsync(context.RequestServices, codeToken.Subject, ct);
        if (user == null)
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsJsonAsync(new { error = "invalid_grant", error_description = "User not found." });
            return;
        }

        // Load scope definitions from DB
        var grantedScopes = await LoadScopesAsync(session, codeToken.Scopes, ct);
        var grantedScopeNames = GrantedNames(grantedScopes);

        var issuer = OidcIssuer.Resolve(context);
        var tokenGenerator = context.RequestServices.GetRequiredService<OidcTokenGenerator>();

        // Generate tokens
        var (accessToken, accessTokenJti) = tokenGenerator.GenerateAccessToken(user, app, issuer, grantedScopes, app.AccessTokenLifetimeMinutes);
        // An id_token asserts an authentication event, which is what the openid scope requests.
        // Issuing one regardless meant a client that only asked for API access still received a
        // signed identity assertion it never sought.
        var idToken = GrantsOpenId(codeToken.Scopes)
            ? tokenGenerator.GenerateIdToken(user, app, issuer, grantedScopes, codeToken.State, app.AccessTokenLifetimeMinutes)
            : null;

        // A refresh token is a long-lived credential and must be asked for. This used to be
        // minted unconditionally, so every browser client silently received a 14-day credential
        // it never requested and could not decline — the widest-reaching thing this endpoint
        // handed out, given away by default.
        var issueRefreshToken = AllowsRefreshTokens(app)
            && codeToken.Scopes.Contains("offline_access", StringComparer.OrdinalIgnoreCase);
        var refreshTokenValue = issueRefreshToken ? tokenGenerator.GenerateRefreshToken() : null;

        // Store access token
        var accessTokenDoc = new OidcToken
        {
            // Keyed by jti so the token can be looked up, and therefore revoked.
            Id = OidcTokenReference.DocumentId(accessTokenJti),
            ApplicationId = app.Id!,
            AuthorizationId = codeToken.AuthorizationId,
            Subject = codeToken.Subject,
            Type = "access_token",
            Scopes = grantedScopeNames,
            Status = "valid",
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddMinutes(app.AccessTokenLifetimeMinutes),
        };

        await session.StoreAsync(accessTokenDoc, ct);

        if (refreshTokenValue != null)
        {
            await session.StoreAsync(new OidcToken
            {
                ApplicationId = app.Id!,
                AuthorizationId = codeToken.AuthorizationId,
                Subject = codeToken.Subject,
                Id = OidcTokenReference.DocumentId(refreshTokenValue),
                Type = "refresh_token",
                Scopes = grantedScopeNames,
                Status = "valid",
                CreatedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddDays(app.RefreshTokenLifetimeDays),
            }, ct);
        }

        // Marking the code redeemed and issuing the tokens are one batch, so losing the race
        // writes nothing at all. The winner holds the tokens; this request gets the same answer
        // a later replay would get.
        if (!await TrySaveAsync(session, ct))
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsJsonAsync(new { error = "invalid_grant", error_description = "Invalid or expired authorization code." });
            return;
        }

        context.Response.ContentType = "application/json";
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Pragma = "no-cache";

        // Built as a dictionary so an unissued refresh token is absent rather than present-and-
        // null: RFC 6749 §5.1 makes refresh_token optional, and a client testing for the key
        // should not have to distinguish "no refresh token" from "a null one".
        var response = new Dictionary<string, object>
        {
            ["access_token"] = accessToken,
            ["token_type"] = "Bearer",
            ["expires_in"] = app.AccessTokenLifetimeMinutes * 60,
        };

        if (idToken != null)
            response["id_token"] = idToken;

        if (refreshTokenValue != null)
            response["refresh_token"] = refreshTokenValue;

        AnnounceScope(response, codeToken.Scopes, grantedScopeNames);

        await context.Response.WriteAsJsonAsync(response);
    }

    /// <summary>
    /// The scopes a token is actually issued with: those the request carried that resolve to a
    /// defined, enabled <c>OidcScope</c>.
    /// <para>
    /// This is what must be recorded, because the JWT is minted from it. Storing the requested
    /// list instead made the token document over-report — and introspection reads the document, so
    /// a resource server asking what a token may do was told about scopes the token does not
    /// carry, which is the dangerous direction to be wrong in. It also made disabling a scope a
    /// half-measure: the JWT dropped it at the next issuance while introspection kept vouching for
    /// it, for as long as the refresh token lived.
    /// </para>
    /// </summary>
    private static List<string> GrantedNames(List<OidcScope> granted)
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
    private static bool AllowsRefreshTokens(OidcApplication app)
        => app.AllowedGrantTypes.Contains("refresh_token", StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether an authentication assertion was actually asked for.</summary>
    private static bool GrantsOpenId(List<string> scopes)
        => scopes.Contains("openid", StringComparer.OrdinalIgnoreCase);

    private static async Task HandleRefreshTokenGrant(HttpContext context, IFormCollection form, CancellationToken ct)
    {
        var clientId = form["client_id"].FirstOrDefault();
        var clientSecret = form["client_secret"].FirstOrDefault();
        var refreshToken = form["refresh_token"].FirstOrDefault();

        if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(refreshToken))
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsJsonAsync(new { error = "invalid_request" });
            return;
        }

        var store = context.RequestServices.GetRequiredService<IDocumentStore>();
        using var session = store.OpenAsyncSession();

        // Rotation spends the presented token, so it races exactly as code redemption does.
        session.Advanced.UseOptimisticConcurrency = true;

        var app = await Authorize.FindApplicationByClientIdAsync(session, clientId, ct);
        if (app == null || !app.Enabled)
        {
            context.Response.StatusCode = 401;
            await context.Response.WriteAsJsonAsync(new { error = "invalid_client" });
            return;
        }

        // The other two grants have always checked this; this one did not, so a client never
        // registered for refresh could still rotate one indefinitely.
        if (!AllowsRefreshTokens(app))
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsJsonAsync(new { error = "unauthorized_client", error_description = "This client is not authorized for refresh_token grant." });
            return;
        }

        // Fail closed: only a client explicitly marked public, holding no secrets, skips
        // authentication. Comparing == "confidential" meant a stray case or space silently
        // disabled client authentication altogether.
        if (!(string.Equals(app.ClientType, "public", StringComparison.OrdinalIgnoreCase) && app.Secrets.Count == 0))
        {
            if (string.IsNullOrEmpty(clientSecret) || !VerifyClientSecret(clientSecret, app.Secrets))
            {
                context.Response.StatusCode = 401;
                await context.Response.WriteAsJsonAsync(new { error = "invalid_client" });
                return;
            }
        }

        // Find refresh token
        // Point-load for the same reason as the authorization-code path above.
        var refreshTokenDoc = await session.LoadAsync<OidcToken>(OidcTokenReference.DocumentId(refreshToken), ct);
        if (refreshTokenDoc is { Type: "refresh_token", Status: not "valid" })
        {
            // Reuse of an already-rotated refresh token. Per RFC 6819 §5.2.2.3 this is
            // treated as theft: revoke the entire chain rather than just refusing.
            // Best-effort, as on the code grant.
            await RevokeAuthorizationChainAsync(session, refreshTokenDoc, ct);
            await TrySaveAsync(session, ct);

            context.Response.StatusCode = 400;
            await context.Response.WriteAsJsonAsync(new { error = "invalid_grant", error_description = "Invalid or expired refresh token." });
            return;
        }

        if (refreshTokenDoc is not { Type: "refresh_token", Status: "valid" })
            refreshTokenDoc = null;

        // Client binding, as on the code grant: without it any client may present another's
        // refresh token and receive a token carrying the original's subject and scopes.
        if (refreshTokenDoc == null
            || refreshTokenDoc.ExpiresAt < DateTime.UtcNow
            || !string.Equals(refreshTokenDoc.ApplicationId, app.Id, StringComparison.Ordinal))
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsJsonAsync(new { error = "invalid_grant", error_description = "Invalid or expired refresh token." });
            return;
        }

        // A refresh may narrow scopes but never widen them: re-intersect against what the
        // client is currently allowed, so revoking a scope from the application takes effect
        // on the next refresh instead of persisting for the token's whole 14-day life.
        refreshTokenDoc.Scopes = refreshTokenDoc.Scopes
            .Where(s => app.AllowedScopes.Contains(s, StringComparer.OrdinalIgnoreCase))
            .ToList();

        // Load user
        var user = await LoadUserAsync(context.RequestServices, refreshTokenDoc.Subject, ct);
        if (user == null)
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsJsonAsync(new { error = "invalid_grant" });
            return;
        }

        // Load scope definitions from DB
        var grantedScopes = await LoadScopesAsync(session, refreshTokenDoc.Scopes, ct);
        var grantedScopeNames = GrantedNames(grantedScopes);

        var issuer = OidcIssuer.Resolve(context);
        var tokenGenerator = context.RequestServices.GetRequiredService<OidcTokenGenerator>();

        // Generate new tokens
        var (newAccessToken, newAccessTokenJti) = tokenGenerator.GenerateAccessToken(user, app, issuer, grantedScopes, app.AccessTokenLifetimeMinutes);
        var newIdToken = GrantsOpenId(refreshTokenDoc.Scopes)
            ? tokenGenerator.GenerateIdToken(user, app, issuer, grantedScopes, null, app.AccessTokenLifetimeMinutes)
            : null;
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
            Type = "access_token",
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
            Id = OidcTokenReference.DocumentId(newRefreshTokenValue),
            Type = "refresh_token",
            Scopes = grantedScopeNames,
            Status = "valid",
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(app.RefreshTokenLifetimeDays),
        };

        await session.StoreAsync(newAccessTokenDoc, ct);
        await session.StoreAsync(newRefreshTokenDoc, ct);

        // As on the code grant: rotation and issuance are one batch, so the loser of a
        // simultaneous rotation writes nothing and is answered as a replay.
        if (!await TrySaveAsync(session, ct))
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsJsonAsync(new { error = "invalid_grant", error_description = "Invalid or expired refresh token." });
            return;
        }

        context.Response.ContentType = "application/json";
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Pragma = "no-cache";

        var response = new Dictionary<string, object>
        {
            ["access_token"] = newAccessToken,
            ["token_type"] = "Bearer",
            ["expires_in"] = app.AccessTokenLifetimeMinutes * 60,
            ["refresh_token"] = newRefreshTokenValue,
        };

        if (newIdToken != null)
            response["id_token"] = newIdToken;

        // A refresh token outlives the configuration it was minted under. Disabling a scope is
        // meant to take capability away, and this is where the client finds out it has.
        AnnounceScope(response, refreshTokenDoc.Scopes, grantedScopeNames);

        await context.Response.WriteAsJsonAsync(response);
    }

    private static async Task HandleClientCredentialsGrant(HttpContext context, IFormCollection form, CancellationToken ct)
    {
        var clientId = form["client_id"].FirstOrDefault();
        var clientSecret = form["client_secret"].FirstOrDefault();
        var scope = form["scope"].FirstOrDefault();

        if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(clientSecret))
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsJsonAsync(new { error = "invalid_request", error_description = "client_id and client_secret are required." });
            return;
        }

        var store = context.RequestServices.GetRequiredService<IDocumentStore>();
        using var session = store.OpenAsyncSession();

        var app = await Authorize.FindApplicationByClientIdAsync(session, clientId, ct);
        if (app == null || !app.Enabled)
        {
            context.Response.StatusCode = 401;
            await context.Response.WriteAsJsonAsync(new { error = "invalid_client" });
            return;
        }

        // Check grant type is allowed
        if (!app.AllowedGrantTypes.Contains("client_credentials", StringComparer.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsJsonAsync(new { error = "unauthorized_client", error_description = "This client is not authorized for client_credentials grant." });
            return;
        }

        // Validate client secret
        if (!VerifyClientSecret(clientSecret, app.Secrets))
        {
            context.Response.StatusCode = 401;
            await context.Response.WriteAsJsonAsync(new { error = "invalid_client", error_description = "Invalid client credentials." });
            return;
        }

        // Parse and validate requested scopes
        var requestedScopes = (scope ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        foreach (var s in requestedScopes)
        {
            if (!app.AllowedScopes.Contains(s, StringComparer.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsJsonAsync(new { error = "invalid_scope", error_description = $"Scope '{s}' is not allowed for this client." });
                return;
            }
        }

        // Requiring the caller to name what it wants, rather than defaulting to everything the
        // client may ever hold. The previous default handed a machine token the client's full
        // authority — api.admin included — to a caller that asked for nothing at all, which is
        // least privilege violated by omission and invisible at the call site.
        if (requestedScopes.Count == 0)
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsJsonAsync(new { error = "invalid_scope", error_description = "scope is required; name the scopes this token needs." });
            return;
        }

        // Load scope definitions from DB
        var grantedScopes = await LoadScopesAsync(session, requestedScopes, ct);
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
            context.Response.StatusCode = 400;
            await context.Response.WriteAsJsonAsync(new
            {
                error = "invalid_scope",
                error_description = $"No enabled scope is defined for: {string.Join(", ", missing)}.",
            });
            return;
        }

        var issuer = OidcIssuer.Resolve(context);
        var tokenGenerator = context.RequestServices.GetRequiredService<OidcTokenGenerator>();

        // Generate access token only (no user, no ID token, no refresh token)
        var (accessToken, accessTokenJti) = tokenGenerator.GenerateAccessToken(null, app, issuer, grantedScopes, app.AccessTokenLifetimeMinutes);

        // Store access token
        var accessTokenDoc = new OidcToken
        {
            // Without this key a machine token was unrevocable outright: nothing tied the JWT
            // to a record, so there was no handle to revoke.
            Id = OidcTokenReference.DocumentId(accessTokenJti),
            ApplicationId = app.Id!,
            Subject = $"client:{app.ClientId}",
            Type = "access_token",
            Scopes = grantedScopeNames,
            Status = "valid",
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddMinutes(app.AccessTokenLifetimeMinutes),
        };

        await session.StoreAsync(accessTokenDoc, ct);
        await session.SaveChangesAsync(ct);

        context.Response.ContentType = "application/json";
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Pragma = "no-cache";

        await context.Response.WriteAsJsonAsync(new
        {
            access_token = accessToken,
            token_type = "Bearer",
            expires_in = app.AccessTokenLifetimeMinutes * 60,
            scope = string.Join(' ', grantedScopeNames),
        });
    }

    internal static async Task<SparkUser?> LoadUserAsync(IServiceProvider serviceProvider, string userId, CancellationToken ct)
    {
        var registry = serviceProvider.GetRequiredService<SparkModuleRegistry>();
        var userType = registry.IdentityUserType ?? typeof(SparkUser);

        var userManagerType = typeof(UserManager<>).MakeGenericType(userType);
        var userManager = serviceProvider.GetRequiredService(userManagerType);

        var findByIdMethod = userManagerType.GetMethod("FindByIdAsync")!;
        var result = await (dynamic)findByIdMethod.Invoke(userManager, [userId])!;
        return result as SparkUser;
    }

    internal static async Task<List<OidcScope>> LoadScopesAsync(IAsyncDocumentSession session, List<string> scopeNames, CancellationToken ct)
    {
        return await session
            .Query<OidcScope>()
            .Where(s => s.Name.In(scopeNames) && s.Enabled)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Revokes every token issued under the same authorization as <paramref name="compromised"/>.
    /// <para>
    /// Presenting a consumed authorization code, or a refresh token that has already been
    /// rotated away, is not a benign retry — the legitimate client holds the successor, so a
    /// replay means the old value leaked. RFC 6819 §5.2.2.3 calls for revoking the whole
    /// chain rather than merely refusing the request, which would leave the attacker's other
    /// stolen tokens working.
    /// </para>
    /// <para>
    /// The sweep is by <c>AuthorizationId</c> and therefore rides an eventually-consistent
    /// index: a token issued moments before the replay may be missed. That is a deliberate
    /// asymmetry — <em>detection</em> is exact (a point-load by id), only the blast-radius
    /// cleanup is best-effort, and it errs toward revoking too little rather than failing
    /// closed on a legitimate request.
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
