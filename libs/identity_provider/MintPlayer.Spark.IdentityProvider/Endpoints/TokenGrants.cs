using System.Security.Claims;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.IdentityProvider.Models;
using MintPlayer.Spark.IdentityProvider.Services;

namespace MintPlayer.Spark.IdentityProvider.Endpoints;

// The device code and token exchange grants (docs/identity_provider_platform_PRD.md D8, I9). A plain
// comment: a second /// summary on a partial class makes the description generator emit it twice.
internal sealed partial class OidcTokenEndpoint<TUser>
{
    public const string TokenExchangeGrantType = "urn:ietf:params:oauth:grant-type:token-exchange";
    private const string AccessTokenType = "urn:ietf:params:oauth:token-type:access_token";

    /// <summary>
    /// RFC 8628 §3.4: the device polls with its device code until the person allowed or denied it.
    /// <c>authorization_pending</c> while waiting, <c>slow_down</c> when it polls faster than the interval,
    /// <c>access_denied</c>, <c>expired_token</c>; then the tokens, once.
    /// </summary>
    private async Task<IResult> HandleDeviceCodeGrant(CancellationToken ct)
    {
        var deviceCode = form["device_code"].FirstOrDefault();
        if (string.IsNullOrEmpty(deviceCode))
            return Results.Json(new { error = "invalid_request", error_description = "device_code is required." }, statusCode: 400);

        using var session = store.OpenAsyncSession();
        session.Advanced.UseOptimisticConcurrency = true;

        var client = await clientAuthenticator.AuthenticateAsync(httpContext, form, session, ct);
        if (!client.Succeeded)
            return client.ToResult(httpContext);
        var app = client.Application!;
        if (!app.AllowedGrantTypes.Contains(OidcDeviceCodes.GrantType, StringComparer.Ordinal))
            return Results.Json(new { error = "unauthorized_client" }, statusCode: 400);

        var device = await session.LoadAsync<OidcToken>(OidcDeviceCodes.DeviceDocumentId(deviceCode), ct);
        if (device is not { Type: OidcTokenTypes.DeviceCode } || device.ApplicationId != app.Id)
            return Results.Json(new { error = "invalid_grant" }, statusCode: 400);
        if (device.ExpiresAt < DateTime.UtcNow)
            return Results.Json(new { error = "expired_token" }, statusCode: 400);

        switch (device.Status)
        {
            case "pending":
                var now = DateTime.UtcNow;
                var tooFast = device.Properties.TryGetValue("last_poll", out var last)
                    && DateTime.TryParse(last, null, System.Globalization.DateTimeStyles.RoundtripKind, out var lastPoll)
                    && now - lastPoll < TimeSpan.FromSeconds(OidcDeviceCodes.IntervalSeconds);
                device.Properties["last_poll"] = now.ToString("O");
                await TrySaveAsync(session, ct);
                return Results.Json(new { error = tooFast ? "slow_down" : "authorization_pending" }, statusCode: 400);
            case "denied":
                return Results.Json(new { error = "access_denied" }, statusCode: 400);
            case "approved":
                break;
            default:
                return Results.Json(new { error = "invalid_grant" }, statusCode: 400);
        }

        device.Status = "redeemed";
        device.RedeemedAt = DateTime.UtcNow;

        var user = await userManager.FindByIdAsync(device.Subject);
        if (user is null)
            return Results.Json(new { error = "invalid_grant", error_description = "User not found." }, statusCode: 400);

        var possession = await proofOfPossession.BindAsync(httpContext, app, client.Certificate, ct);
        if (!possession.Succeeded)
            return Results.Json(new { error = possession.Error, error_description = possession.ErrorDescription }, statusCode: 400);

        var grantedScopes = await Token.LoadScopesAsync(session, device.Scopes, ct);
        var grantedScopeNames = GrantedNames(grantedScopes);
        var issuer = oidcIssuer.Resolve(httpContext.Request);

        var (accessToken, jti) = tokenGenerator.GenerateAccessToken(user, app, issuer, grantedScopes, app.AccessTokenLifetimeMinutes,
            device.Properties, possession.Confirmation);
        var idToken = GrantsOpenId(device.Scopes)
            ? tokenGenerator.GenerateIdToken(user, app, issuer, grantedScopes, null, app.EffectiveIdTokenLifetimeMinutes(),
                accessToken: accessToken, authTime: device.AuthTime, properties: device.Properties)
            : null;
        if (idToken is not null)
        {
            idToken = await jwe.EncryptAsync(idToken, app, app.IdTokenEncryptedResponseAlg, app.IdTokenEncryptedResponseEnc, ct);
            if (idToken is null)
                return Results.Json(new { error = "invalid_client", error_description = "The client asked for encrypted id_tokens and has no usable encryption key." }, statusCode: 400);
        }

        await session.StoreExpiringAsync(new OidcToken
        {
            Id = OidcTokenReference.DocumentId(jti),
            ApplicationId = app.Id!,
            AuthorizationId = device.AuthorizationId,
            Subject = device.Subject,
            Type = OidcTokenTypes.AccessToken,
            Scopes = grantedScopeNames,
            Status = "valid",
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddMinutes(app.AccessTokenLifetimeMinutes),
            SessionId = device.Properties.GetValueOrDefault("sid"),
        }, ct);

        string? refreshToken = null;
        if (AllowsRefreshTokens(app))
        {
            refreshToken = tokenGenerator.GenerateRefreshToken();
            await session.StoreExpiringAsync(new OidcToken
            {
                Id = OidcTokenReference.DocumentId(refreshToken),
                ApplicationId = app.Id!,
                AuthorizationId = device.AuthorizationId,
                Subject = device.Subject,
                Type = OidcTokenTypes.RefreshToken,
                Scopes = grantedScopeNames,
                Status = "valid",
                CreatedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddDays(app.RefreshTokenLifetimeDays),
                AuthTime = device.AuthTime,
                Properties = WithBinding(device.Properties, possession),
            }, ct);
        }

        if (!await TrySaveAsync(session, ct))
            return Results.Json(new { error = "invalid_grant" }, statusCode: 400);
        await OidcGrantUsage.TouchAsync(store, device.AuthorizationId, ct);

        httpContext.Response.Headers.CacheControl = "no-store";
        var response = new Dictionary<string, object>
        {
            ["access_token"] = accessToken,
            ["token_type"] = possession.TokenType,
            ["expires_in"] = app.AccessTokenLifetimeMinutes * 60,
            ["scope"] = string.Join(' ', grantedScopeNames),
        };
        if (idToken is not null) response["id_token"] = idToken;
        if (refreshToken is not null) response["refresh_token"] = refreshToken;
        return Results.Json(response);
    }

    /// <summary>
    /// RFC 8693: a client (typically an API calling another API) exchanges an access token this provider
    /// issued for one addressed to another resource. The new token keeps the subject and never widens its
    /// scopes. With an <c>actor_token</c> it is delegation: the token says who acts (<c>act</c>). Without
    /// one it is impersonation, allowed only for a client registered with <c>AllowImpersonation</c>; even
    /// then the token names the calling client as actor, so nothing passes for the user acting alone.
    /// </summary>
    private async Task<IResult> HandleTokenExchangeGrant(CancellationToken ct)
    {
        var subjectToken = form["subject_token"].FirstOrDefault();
        var subjectTokenType = form["subject_token_type"].FirstOrDefault();
        var actorToken = form["actor_token"].FirstOrDefault();
        var requestedScope = form["scope"].FirstOrDefault();
        var audiences = form["audience"].Concat(form["resource"]).Where(a => !string.IsNullOrEmpty(a)).Select(a => a!).ToList();

        if (string.IsNullOrEmpty(subjectToken) || subjectTokenType != AccessTokenType)
            return Results.Json(new { error = "invalid_request", error_description = "subject_token must be an access token." }, statusCode: 400);

        using var session = store.OpenAsyncSession();
        var client = await clientAuthenticator.AuthenticateAsync(httpContext, form, session, ct);
        if (!client.Succeeded)
            return client.ToResult(httpContext);
        var app = client.Application!;
        if (client.Method == OidcClientAuthMethods.None || !app.AllowedGrantTypes.Contains(TokenExchangeGrantType, StringComparer.Ordinal))
            return Results.Json(new { error = "unauthorized_client" }, statusCode: 400);

        var issuer = oidcIssuer.Resolve(httpContext.Request);
        var subject = await AccessTokens.ResolveAsync(session, signingKeyService, subjectToken, issuer, ct);
        if (subject is not { IsActive: true, Record: { } record })
            return Results.Json(new { error = "invalid_grant", error_description = "The subject token is not active." }, statusCode: 400);

        string? actorSubject = null;
        if (!string.IsNullOrEmpty(actorToken))
        {
            var actor = await AccessTokens.ResolveAsync(session, signingKeyService, actorToken, issuer, ct);
            if (actor is not { IsActive: true, Record: { } actorRecord })
                return Results.Json(new { error = "invalid_grant", error_description = "The actor token is not active." }, statusCode: 400);
            // The actor is the caller: its token must have been issued to this client, and it cannot be
            // the subject token over again. Otherwise any client could pass the user's own token as the
            // actor and get a delegation it was never given, bypassing AllowImpersonation.
            if (!string.Equals(actorRecord.ApplicationId, app.Id, StringComparison.Ordinal)
                || string.Equals(actorRecord.Id, record.Id, StringComparison.Ordinal))
                return Results.Json(new { error = "invalid_grant", error_description = "The actor token must be this client's own token." }, statusCode: 400);
            actorSubject = actor.Subject ?? actor.ClientId ?? app.ClientId;
        }
        else if (!app.AllowImpersonation)
        {
            return Results.Json(new { error = "invalid_grant", error_description = "Impersonation is not allowed for this client; send an actor_token." }, statusCode: 400);
        }

        // Never wider than the subject token, and only scopes this client may hold.
        var subjectScopes = (subject.Scope ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var wanted = string.IsNullOrEmpty(requestedScope) ? subjectScopes : requestedScope.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var scopes = wanted
            .Where(s => subjectScopes.Contains(s, StringComparer.Ordinal) && app.ScopeNames().Contains(s, StringComparer.OrdinalIgnoreCase))
            .ToList();
        scopes = OidcApplicationAccess.AvailableScopes(app, userId: "", scopes);
        var grantedScopes = await Token.LoadScopesAsync(session, scopes, ct);
        if (grantedScopes.Count == 0)
            return Results.Json(new { error = "invalid_scope" }, statusCode: 400);

        var properties = new Dictionary<string, string>();
        if (audiences.Count > 0)
            properties["resource"] = string.Join(' ', audiences);
        var user = await userManager.FindByIdAsync(record.Subject);

        var (accessToken, jti) = tokenGenerator.GenerateAccessToken(user, app, issuer, grantedScopes, app.AccessTokenLifetimeMinutes, properties,
            confirmation: null, actor: new Dictionary<string, object> { ["sub"] = actorSubject ?? $"client:{app.ClientId}" });

        await session.StoreExpiringAsync(new OidcToken
        {
            Id = OidcTokenReference.DocumentId(jti),
            ApplicationId = app.Id!,
            AuthorizationId = record.AuthorizationId,
            Subject = record.Subject,
            Type = OidcTokenTypes.AccessToken,
            Scopes = GrantedNames(grantedScopes),
            Status = "valid",
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddMinutes(app.AccessTokenLifetimeMinutes),
        }, ct);
        await session.SaveChangesAsync(ct);

        httpContext.Response.Headers.CacheControl = "no-store";
        return Results.Json(new Dictionary<string, object>
        {
            ["access_token"] = accessToken,
            ["issued_token_type"] = AccessTokenType,
            ["token_type"] = "Bearer",
            ["expires_in"] = app.AccessTokenLifetimeMinutes * 60,
            ["scope"] = string.Join(' ', GrantedNames(grantedScopes)),
        });
    }
}
