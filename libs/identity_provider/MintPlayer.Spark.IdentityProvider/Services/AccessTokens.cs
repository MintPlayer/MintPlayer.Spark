using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using MintPlayer.Spark.IdentityProvider.Models;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.IdentityProvider.Services;

/// <summary>
/// Resolves a presented access token to the record that governs it.
/// <para>
/// Access tokens are self-contained JWTs, so a signature check alone answers "did we issue
/// this?" — not "is it still good?". Without the second question nothing can ever be taken
/// back: <c>Status = "revoked"</c> was written to the database and read by nobody, so
/// introspection reported <c>active: true</c> for a revoked token, which is the one question
/// RFC 7662 exists to answer, and <c>/connect/userinfo</c> kept serving claims. Direct
/// revocation could not work at all — the record's id was a random Guid with the JWT text in
/// a <c>Payload</c> field, so there was nothing to look it up by, and
/// <c>client_credentials</c> tokens were unrevocable outright.
/// </para>
/// <para>
/// Each access token now carries a <c>jti</c>, and its record is keyed by that. Every consumer
/// goes through this one type so they cannot drift on what "still valid" means: signature and
/// issuer must check out, the record must exist and be <c>valid</c>, and it must not have
/// expired. A missing record fails closed — either it was reaped, in which case the token has
/// expired anyway, or the token was never ours.
/// </para>
/// </summary>
internal static class AccessTokens
{
    /// <summary>Resolves the token, or null if it is not a well-formed token we signed.</summary>
    public static async Task<ResolvedAccessToken?> ResolveAsync(
        IAsyncDocumentSession session,
        OidcSigningKeyService keys,
        string token,
        string issuer,
        CancellationToken ct)
    {
        var handler = new JsonWebTokenHandler();
        var validation = await handler.ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = issuer,
            ValidateAudience = false,
            // Expiry is judged below so callers can distinguish "expired" from "not ours"
            // rather than both surfacing as a validation failure.
            ValidateLifetime = false,
            IssuerSigningKey = keys.GetSigningKey(),
        });

        if (!validation.IsValid || validation.SecurityToken is not JsonWebToken jwt)
            return null;

        var jti = jwt.Id;
        var record = string.IsNullOrEmpty(jti)
            ? null
            : await session.LoadAsync<OidcToken>(OidcTokenReference.DocumentId(jti), ct);

        if (record is not { Type: "access_token" })
            record = null;

        // The user's standing consent, when there is a user. Loaded here rather than in
        // IsActive because that is a pure property and this is a database read — and resolved
        // once, here, so all three consumers (introspection, userinfo, revocation) get the same
        // answer instead of each deciding for itself whether consent still matters.
        //
        // An empty AuthorizationId means no user grant exists: client_credentials tokens, and
        // tokens minted before the id was threaded through. Those must stay active — treating
        // "no grant" as "withdrawn grant" would kill every machine token in the system.
        OidcAuthorization? grant = null;
        if (record is { AuthorizationId.Length: > 0 })
            grant = await session.LoadAsync<OidcAuthorization>(record.AuthorizationId, ct);

        return new ResolvedAccessToken(jwt, validation.Claims, record, grant);
    }
}

/// <summary>A presented access token together with the record that governs its life.</summary>
internal sealed record ResolvedAccessToken(
    JsonWebToken Jwt,
    IDictionary<string, object> Claims,
    OidcToken? Record,
    OidcAuthorization? Grant = null)
{
    /// <summary>
    /// True only if we issued it, it has not been revoked, it has not expired, and the user's
    /// consent still stands behind it. A token whose record has gone fails closed.
    /// <para>
    /// The consent clause cannot reach a resource server that validates the JWT offline against
    /// JWKS — nothing in this package can, which is why withdrawal also revokes the refresh
    /// token and why access-token lifetimes are meant to be short. It does cover every consumer
    /// that asks us: <c>/connect/introspect</c> and <c>/connect/userinfo</c>.
    /// </para>
    /// </summary>
    public bool IsActive => Record is { Status: "valid" }
        && Jwt.ValidTo > DateTime.UtcNow
        && !GrantWithdrawn;

    /// <summary>
    /// A grant that exists and is no longer valid, or one that should exist and has been
    /// deleted. A token with no <c>AuthorizationId</c> has no grant to withdraw and is unaffected.
    /// </summary>
    private bool GrantWithdrawn
        => Record is { AuthorizationId.Length: > 0 } && Grant is not { Status: "valid" };

    public string? Subject => Claim("sub");
    public string? Scope => Claim("scope");
    public string? ClientId => Claim("client_id");

    /// <summary>
    /// The audiences the token was minted for. Not validated here — a token is "active"
    /// regardless of who it was meant for — but surfaced so introspection can report it and a
    /// resource server can decide for itself.
    /// </summary>
    public IReadOnlyList<string> Audiences => Jwt.Audiences?.ToList() ?? [];

    private string? Claim(string type) => Claims.TryGetValue(type, out var value) ? value?.ToString() : null;
}
