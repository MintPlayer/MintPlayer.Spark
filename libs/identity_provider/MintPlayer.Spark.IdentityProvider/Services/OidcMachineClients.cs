using MintPlayer.Spark.IdentityProvider.Models;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;

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
    public static async Task<OidcApplication> CreateAsync(IAsyncDocumentSession session, string displayName, string createdBy, string? homepageUrl, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(createdBy);
        var app = new OidcApplication
        {
            Id = "OidcApplications/" + Guid.NewGuid().ToString("N"),
            ClientId = OidcClientIdReservation.GenerateClientId(),
            DisplayName = displayName,
            ClientType = "confidential",
            TokenEndpointAuthMethod = OidcClientAuthMethods.PrivateKeyJwt,
            AllowedGrantTypes = ["client_credentials"],
            HomepageUrl = homepageUrl,
            CreatedAt = DateTime.UtcNow,
            CreatedBy = createdBy,
        };
        await OidcClientIdReservation.ReserveAsync(session, app);
        await session.StoreAsync(app, app.Id, ct);
        return app;
    }

    /// <summary>
    /// Deletes the client: every token and grant it holds is revoked first (<see cref="OidcDisableCascade"/>), then the
    /// document goes and its client id is released. A missing client is not an error.
    /// </summary>
    public static async Task DeleteAsync(IDocumentStore store, string applicationId, CancellationToken ct = default)
    {
        await OidcDisableCascade.RevokeApplicationAsync(store, applicationId, ct);
        using var session = store.OpenAsyncSession();
        if (await session.LoadAsync<OidcApplication>(applicationId, ct) is not { } app)
            return;
        await OidcClientIdReservation.ReleaseAsync(session, app);
        session.Delete(app);
        await session.SaveChangesAsync(ct);
    }
}
