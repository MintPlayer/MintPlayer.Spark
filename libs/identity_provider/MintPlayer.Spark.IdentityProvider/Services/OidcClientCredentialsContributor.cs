using MintPlayer.Spark.IdentityProvider.Models;

namespace MintPlayer.Spark.IdentityProvider.Services;

/// <summary>
/// Shapes a <c>client_credentials</c> token for a request another package recognises (#691 D16): Spark Apps turns
/// <c>installation_id=…</c> into an installation token. Registered contributors are asked in order, after the client
/// authenticated and was found allowed the grant; the first non-null answer decides, and the plain grant runs when all
/// answer null.
/// </summary>
/// <remarks>
/// The contribution replaces the scope rules of the plain grant: the scopes it returns are what the token carries,
/// addressed to <see cref="OidcClientCredentialsContribution.Audience"/>. Its <see cref="OidcClientCredentialsContribution.AuthorizationId"/>
/// names an <see cref="OidcGrant"/> document the contributor keeps; introspection refuses the token as soon as that
/// grant is revoked (<c>OidcGrants.PermitsAsync</c>), so the contributor ends a token early by revoking its grant.
/// </remarks>
public interface IOidcClientCredentialsContributor
{
    /// <summary>Null: not this contributor's request.</summary>
    Task<OidcClientCredentialsContribution?> ContributeAsync(OidcClientCredentialsRequest request, CancellationToken cancellationToken);
}

/// <param name="Application">The authenticated client.</param>
/// <param name="AuthenticationMethod">How it authenticated, e.g. <c>private_key_jwt</c>.</param>
/// <param name="Parameters">The posted form, first value per name.</param>
/// <param name="RequestedScopes">The <c>scope</c> parameter, split; may be empty.</param>
public sealed record OidcClientCredentialsRequest(
    OidcApplication Application,
    string AuthenticationMethod,
    IReadOnlyDictionary<string, string> Parameters,
    IReadOnlyList<string> RequestedScopes);

public sealed record OidcClientCredentialsContribution
{
    /// <summary>Set to refuse the request with this OAuth error (<c>invalid_request</c>, <c>invalid_grant</c>, …).</summary>
    public string? Error { get; init; }
    public string? ErrorDescription { get; init; }

    /// <summary>The token's scopes.</summary>
    public IReadOnlyList<string> Scopes { get; init; } = [];

    /// <summary>The token's audience; the client id when null.</summary>
    public string? Audience { get; init; }

    /// <summary>Extra string claims. Registered JWT and OAuth claim names (<c>sub</c>, <c>scope</c>, <c>client_id</c>, …) are ignored.</summary>
    public IReadOnlyDictionary<string, string> Claims { get; init; } = new Dictionary<string, string>();

    /// <summary>Minutes; the client's <see cref="OidcApplication.AccessTokenLifetimeMinutes"/> when null.</summary>
    public int? LifetimeMinutes { get; init; }

    /// <summary>The <see cref="OidcGrant"/> id that keeps this token alive.</summary>
    public string? AuthorizationId { get; init; }

    /// <summary>The token record's subject, e.g. <c>installation:…</c>; <c>client:{client id}</c> when null.</summary>
    public string? Subject { get; init; }

    public static OidcClientCredentialsContribution Refuse(string error, string description) => new() { Error = error, ErrorDescription = description };
}
