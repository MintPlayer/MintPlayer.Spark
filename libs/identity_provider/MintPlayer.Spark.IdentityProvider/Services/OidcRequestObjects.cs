using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Extensions.Primitives;
using MintPlayer.Spark.IdentityProvider.Models;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.IdentityProvider.Services;

/// <summary>The outcome of resolving an authorization request into plain parameters.</summary>
internal sealed record OidcResolvedRequest(
    OidcAuthorizeParameters? Parameters,
    bool FromPushedRequest = false,
    bool FromRequestObject = false,
    string? Error = null,
    string? ErrorDescription = null);

/// <summary>
/// Pushed authorization requests (PAR, RFC 9126) and signed request objects (JAR, RFC 9101)
/// (<c>docs/identity_provider_platform_PRD.md</c> D8).
/// <list type="bullet">
/// <item><b>PAR:</b> the client posts its parameters to <c>/connect/par</c>, authenticated, and gets a
/// <c>request_uri</c> valid for 90 seconds; the browser then carries only that and the client id. The
/// pushed parameters are stored as an <see cref="OidcToken"/> of type <see cref="OidcTokenTypes.PushedRequest"/>.</item>
/// <item><b>JAR:</b> the parameters travel as a JWT signed with one of the client's keys, <c>iss</c> = the
/// client id, <c>aud</c> = this issuer. Only the request object's parameters are used (RFC 9101 §6.3).</item>
/// </list>
/// A request object can itself be pushed: PAR validates it at push time.
/// </summary>
internal sealed class OidcRequestObjects(IDocumentStore store, OidcClientKeys clientKeys)
{
    /// <summary>How long a pushed request can be redeemed (RFC 9126 recommends a short lifetime).</summary>
    public static readonly TimeSpan PushedRequestLifetime = TimeSpan.FromSeconds(90);

    public async Task<OidcResolvedRequest> ResolveAsync(IAsyncDocumentSession session, OidcAuthorizeParameters parameters, string issuer, CancellationToken ct)
    {
        if (parameters.RequestUri is not null && parameters.Request is not null)
            return new(null, Error: "invalid_request", ErrorDescription: "Use request or request_uri, not both.");

        if (parameters.RequestUri is not null)
        {
            if (!parameters.IsPushed)
                return new(null, Error: "request_uri_not_supported", ErrorDescription: "Only request_uri values from /connect/par are accepted.");

            var handle = parameters.RequestUri[OidcAuthorizeParameters.PushedRequestUriPrefix.Length..];
            var pushed = await session.LoadAsync<OidcToken>(OidcTokenReference.DocumentId("par:" + handle), ct);
            if (pushed is not { Type: OidcTokenTypes.PushedRequest } || pushed.ExpiresAt < DateTime.UtcNow)
                return new(null, Error: "invalid_request_uri", ErrorDescription: "The request_uri is unknown or expired.");

            var app = await session.LoadAsync<OidcApplication>(pushed.ApplicationId, ct);
            if (app is null || app.ClientId != parameters.ClientId)
                return new(null, Error: "invalid_request_uri", ErrorDescription: "The request_uri was pushed by another client.");

            var stored = OidcAuthorizeParameters.From(name =>
                pushed.Properties.TryGetValue(name, out var value) ? new StringValues(value.Split('\u001f')) : StringValues.Empty);
            // Kept as the request_uri, so a bounce through sign-in carries only the reference.
            return new(stored with { RequestUri = parameters.RequestUri, Request = null }, FromPushedRequest: true,
                FromRequestObject: pushed.Properties.ContainsKey("_jar"));
        }

        if (parameters.Request is not null)
        {
            var fromObject = await ParseRequestObjectAsync(session, parameters.Request, parameters.ClientId, issuer, ct);
            return fromObject.Error is not null ? fromObject : fromObject with { FromRequestObject = true };
        }

        return new(parameters);
    }

    /// <summary>
    /// Validates a request object (JAR) and returns its parameters: signed (never <c>alg=none</c>) by one
    /// of the client's keys, <c>iss</c> = the client, <c>aud</c> = this issuer, unexpired if it carries
    /// <c>exp</c>, and its <c>jti</c> used once.
    /// </summary>
    public async Task<OidcResolvedRequest> ParseRequestObjectAsync(
        IAsyncDocumentSession session, string requestObject, string? clientId, string issuer, CancellationToken ct)
    {
        JsonWebToken unverified;
        try { unverified = new JsonWebToken(requestObject); }
        catch (ArgumentException) { return new(null, Error: "invalid_request_object", ErrorDescription: "The request object is not a JWT."); }

        var claimedClient = clientId ?? unverified.Issuer;
        var app = string.IsNullOrEmpty(claimedClient) ? null : await OidcAuthorizationFlow.FindApplicationByClientIdAsync(session, claimedClient, ct);
        if (app is null)
            return new(null, Error: "invalid_request_object", ErrorDescription: "Unknown client.");

        var keys = await clientKeys.GetSigningKeysAsync(app, ct);
        var result = await new JsonWebTokenHandler().ValidateTokenAsync(requestObject, new TokenValidationParameters
        {
            ValidIssuer = app.ClientId,
            ValidAudience = issuer,
            IssuerSigningKeys = keys,
            RequireSignedTokens = true,
            RequireExpirationTime = false,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(1),
        });
        if (!result.IsValid || result.SecurityToken is not JsonWebToken jwt)
            return new(null, Error: "invalid_request_object", ErrorDescription: "The request object's signature or claims are not valid.");

        if (jwt.GetPayloadValue<string?>("client_id") is { } innerClient && innerClient != app.ClientId)
            return new(null, Error: "invalid_request_object", ErrorDescription: "client_id does not match the request object.");

        if (!string.IsNullOrEmpty(jwt.Id)
            && !await OidcReplayCache.TryUseAsync(store, $"jar:{app.ClientId}:{jwt.Id}", jwt.ValidTo == DateTime.MinValue ? DateTime.UtcNow.AddHours(1) : jwt.ValidTo, ct))
            return new(null, Error: "invalid_request_object", ErrorDescription: "The request object was already used.");

        var parameters = OidcAuthorizeParameters.From(name => Read(jwt, name)) with { ClientId = app.ClientId, Request = null, RequestUri = null };
        return new(parameters);
    }

    private static StringValues Read(JsonWebToken jwt, string name)
    {
        if (!jwt.TryGetPayloadValue<JsonElement>(name, out var value))
            return StringValues.Empty;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Array => new StringValues([.. value.EnumerateArray().Select(v => v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText())]),
            JsonValueKind.Number => value.GetRawText(),
            // The claims parameter is an object inside a request object; it travels on as JSON text.
            JsonValueKind.Object => value.GetRawText(),
            _ => StringValues.Empty,
        };
    }

    /// <summary>
    /// Stores pushed parameters (RFC 9126 §2.1) for <paramref name="app"/> and returns the <c>request_uri</c>.
    /// The pushed values are stored as given; they are validated again when the browser redeems them,
    /// exactly as a request by value is.
    /// </summary>
    public async Task<string> PushAsync(IAsyncDocumentSession session, OidcApplication app, OidcAuthorizeParameters parameters, bool fromRequestObject, CancellationToken ct)
    {
        var handle = OidcTokenReference.GenerateValue();
        var properties = new Dictionary<string, string>();
        void Put(string name, string? value) { if (!string.IsNullOrEmpty(value)) properties[name] = value; }
        Put("client_id", app.ClientId);
        Put("redirect_uri", parameters.RedirectUri);
        Put("response_type", parameters.ResponseType);
        Put("response_mode", parameters.ResponseMode);
        Put("scope", parameters.Scope);
        Put("state", parameters.State);
        Put("nonce", parameters.Nonce);
        Put("code_challenge", parameters.CodeChallenge);
        Put("code_challenge_method", parameters.CodeChallengeMethod);
        Put("prompt", parameters.Prompt);
        Put("max_age", parameters.MaxAge);
        Put("login_hint", parameters.LoginHint);
        Put("ui_locales", parameters.UiLocales);
        Put("acr_values", parameters.AcrValues);
        Put("claims", parameters.Claims);
        Put("id_token_hint", parameters.IdTokenHint);
        Put("include_granted_scopes", parameters.IncludeGrantedScopes);
        if (parameters.Resources.Count > 0) properties["resource"] = string.Join('\u001f', parameters.Resources);
        if (fromRequestObject) properties["_jar"] = "1";

        var pushed = new OidcToken
        {
            Id = OidcTokenReference.DocumentId("par:" + handle),
            Type = OidcTokenTypes.PushedRequest,
            ApplicationId = app.Id!,
            Status = "valid",
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.Add(PushedRequestLifetime),
            Properties = properties,
        };
        await session.StoreExpiringAsync(pushed, ct);
        await session.SaveChangesAsync(ct);
        return OidcAuthorizeParameters.PushedRequestUriPrefix + handle;
    }
}
