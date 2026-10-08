namespace MintPlayer.Spark.IdentityProvider.Models;

/// <summary>
/// Every short-lived thing the provider issues or holds: the <c>OidcTokens</c> collection
/// (<c>docs/identity_provider_platform_PRD.md</c> D1). <see cref="Type"/> says which
/// (<see cref="OidcTokenTypes"/>): authorization codes, access and refresh tokens, pending authorize
/// requests, pushed authorization requests, device codes, sessions and DPoP proof ids.
/// <para>
/// Every document carries <c>@expires</c> at <see cref="ExpiresAt"/>, so RavenDB deletes it. That is
/// housekeeping only: the expiration sweep runs every 36 hours on Community, so every read checks
/// <see cref="ExpiresAt"/> itself.
/// </para>
/// </summary>
public class OidcToken
{
    public string? Id { get; set; }
    public string ApplicationId { get; set; } = string.Empty;
    /// <summary>The <see cref="OidcGrant"/> the token was issued under; empty for machine tokens.</summary>
    public string AuthorizationId { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    /// <summary>What the document is: one of <see cref="OidcTokenTypes"/>.</summary>
    public string Type { get; set; } = string.Empty;
    // No ReferenceId: the bearer value is never persisted. The document id is its SHA-256
    // (see OidcTokenReference), so lookups are point-loads and a database leak yields
    // nothing replayable.
    public string? CodeChallenge { get; set; }
    public string? CodeChallengeMethod { get; set; }
    public string? RedirectUri { get; set; }
    public List<string> Scopes { get; set; } = [];
    // No Payload: the signed JWT was stored in cleartext, written three times and read never.
    // Access-token records are keyed by the token's jti instead (see AccessTokens), which is
    // what makes them revocable — storing the token bought nothing but a liability.
    /// <summary><c>valid</c>, <c>redeemed</c>, <c>revoked</c>; a pending authorize request is <c>pending</c>.</summary>
    public string Status { get; set; } = "valid";
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? RedeemedAt { get; set; }
    public string? State { get; set; }
    /// <summary>The <c>nonce</c> of the authorize request, for the id_token (O20: its own field, never folded into State).</summary>
    public string? Nonce { get; set; }
    /// <summary>
    /// When the user behind this code or refresh token last authenticated interactively, for the
    /// id_token's <c>auth_time</c>. Carried from the authorization request to the code, and from the
    /// code to its refresh token, because a refresh is not a re-authentication.
    /// </summary>
    public DateTimeOffset? AuthTime { get; set; }
    /// <summary>The session the token belongs to (<c>sid</c>), for logout.</summary>
    public string? SessionId { get; set; }
    /// <summary>
    /// Protocol state that only some types carry: the authorize parameters of a pending or pushed
    /// request (<c>prompt</c>, <c>max_age</c>, <c>claims</c>, <c>resource</c>, <c>response_mode</c>…),
    /// a device code's user code, a session's authentication methods, a token's <c>cnf</c> binding.
    /// </summary>
    public Dictionary<string, string> Properties { get; set; } = [];
}

/// <summary>The values of <see cref="OidcToken.Type"/>.</summary>
public static class OidcTokenTypes
{
    public const string AuthorizationCode = "authorization_code";
    public const string AccessToken = "access_token";
    public const string RefreshToken = "refresh_token";
    /// <summary>A validated authorize request waiting for sign-in or consent. Replaces <c>OidcAuthorizationRequests</c>.</summary>
    public const string AuthorizationRequest = "authorization_request";
    /// <summary>A pushed authorization request (PAR, RFC 9126), redeemed by its <c>request_uri</c>.</summary>
    public const string PushedRequest = "pushed_request";
    /// <summary>A device authorization (RFC 8628), keyed by its device code; its user code is in <see cref="OidcToken.Properties"/>.</summary>
    public const string DeviceCode = "device_code";
    /// <summary>A user's session at the provider (<c>sid</c>), for back- and front-channel logout.</summary>
    public const string Session = "session";
    /// <summary>A DPoP proof's <c>jti</c>, kept for its lifetime so a replay is refused.</summary>
    public const string DpopProof = "dpop_jti";
    /// <summary>An initial access token for dynamic client registration (RFC 7591).</summary>
    public const string RegistrationAccessToken = "registration_access_token";
}
