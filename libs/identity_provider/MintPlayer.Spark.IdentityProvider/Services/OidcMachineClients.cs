using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.IdentityProvider.Models;
using MintPlayer.Spark.Abstractions.Storage;

namespace MintPlayer.Spark.IdentityProvider.Services;

/// <summary>
/// Machine clients that another package registers for its own users (#691 D29, Spark Apps): a confidential
/// <c>client_credentials</c> client that authenticates with <c>private_key_jwt</c> only, so it never holds a secret.
/// The caller sets <see cref="OidcApplication.Jwks"/> (the public halves of the keys it issued) and keeps the application
/// id. The client id is reserved like every other (<see cref="OidcClientIdReservation"/>).
/// </summary>
public static class OidcMachineClients
{
    /// <summary>Creates the client in <paramref name="session"/>; it exists once the session saves.</summary>
    public static async Task<OidcApplication> CreateAsync(ISparkSession session, string displayName, string createdBy, string? homepageUrl, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(createdBy);
        var app = new OidcApplication
        {
            Id = Id<OidcApplication>.FromTrusted("oidcapplications/" + Guid.NewGuid().ToString("N")),
            ClientId = OidcClientIdReservation.GenerateClientId(),
            DisplayName = displayName,
            ClientType = "confidential",
            TokenEndpointAuthMethod = OidcClientAuthMethods.PrivateKeyJwt,
            AllowedGrantTypes = ["client_credentials"],
            HomepageUrl = homepageUrl,
            CreatedAt = DateTime.UtcNow,
            CreatedBy = createdBy,
        };
        await OidcClientIdReservation.ReserveAsync(session, app, ct);
        await session.StoreAsync(app, app.Id.Value, ct);
        return app;
    }

    /// <summary>
    /// The grant that keeps a client's contributed tokens alive (#691 D16, <see cref="IOidcClientCredentialsContributor"/>):
    /// one per <paramref name="subject"/> (e.g. <c>installation:in_…</c>), its id derived from the pair. Created valid in
    /// <paramref name="session"/>; returns its id, for <see cref="OidcClientCredentialsContribution.AuthorizationId"/>.
    /// </summary>
    public static async Task<string> CreateGrantAsync(ISparkSession session, string applicationId, string subject, IEnumerable<string> scopes, CancellationToken ct = default)
    {
        var grant = new OidcGrant
        {
            Id = Id<OidcGrant>.FromTrusted(OidcGrantReference.DocumentId(subject, applicationId)),
            ApplicationId = applicationId,
            Subject = subject,
            Status = "valid",
            GrantedScopes = scopes.ToList(),
            CreatedAt = DateTime.UtcNow,
        };
        await session.StoreAsync(grant, grant.Id.Value, ct);
        return grant.Id.Value;
    }

    /// <summary>
    /// Ends every token issued under the grant so far (<see cref="OidcGrants.PermitsAsync"/> refuses a token older than
    /// <see cref="OidcGrant.LastRevokedAt"/>); <paramref name="revoke"/> also ends the grant itself, so no token under it
    /// is honoured again. In <paramref name="session"/>; the caller saves.
    /// </summary>
    public static async Task WithdrawGrantAsync(ISparkSession session, string grantId, bool revoke, CancellationToken ct = default)
    {
        if (await session.LoadAsync<OidcGrant>(grantId, ct) is not { } grant)
            return;
        var now = DateTime.UtcNow;
        grant.LastRevokedAt = now;
        if (revoke)
        {
            grant.Status = "revoked";
            grant.RevokedAt = now;
        }
    }

    /// <summary>
    /// Deletes the client: every token and grant it holds is revoked first (<see cref="OidcDisableCascade"/>), then the
    /// document goes and its client id is released. A missing client is not an error.
    /// </summary>
    public static async Task DeleteAsync(ISparkStore store, string applicationId, CancellationToken ct = default)
    {
        await OidcDisableCascade.RevokeApplicationAsync(store, applicationId, ct);
        using var session = store.OpenSession();
        if (await session.LoadAsync<OidcApplication>(applicationId, ct) is not { } app)
            return;
        OidcClientIdReservation.Release(session, app);
        session.Delete(app);
        await session.SaveChangesAsync(ct);
    }
}
