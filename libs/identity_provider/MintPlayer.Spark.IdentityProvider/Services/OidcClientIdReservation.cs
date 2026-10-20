using System.Security.Cryptography;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.IdentityProvider.Models;
using MintPlayer.Spark.Claims;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.IdentityProvider.Services;

/// <summary>
/// Makes a client id unique atomically (O17, <c>docs/identity_provider_platform_PRD.md</c> D1): a
/// compare-exchange value <c>oidc/client-ids/&lt;client id&gt;</c> naming the application document
/// that owns it.
/// <para>
/// The reservation is written before the application is saved, outside its transaction. A save that
/// then fails leaves a reservation naming a document that does not exist; the next save of that
/// client id finds the owner missing and takes the reservation over, so a failed save never burns a
/// client id.
/// </para>
/// </summary>
internal static class OidcClientIdReservation
{
    private const string KeyPrefix = "oidc/client-ids/";

    /// <summary>A new client id: 22 url-safe characters, 128 bits of randomness.</summary>
    public static string GenerateClientId()
        => Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string Key(string clientId) => KeyPrefix + clientId.ToLowerInvariant();

    /// <summary>
    /// The claim family (commerce X12): the value is the owning application's document id, never expires, and is
    /// abandoned once that document is gone — the owner was never saved, or was deleted without releasing.
    /// </summary>
    private static SparkClaimKind<string> Kind(IAsyncDocumentSession session) => new(KeyPrefix, owner => owner)
    {
        OwnerComparer = StringComparer.OrdinalIgnoreCase,
        IsAbandonedAsync = async (claim, _) => !string.IsNullOrEmpty(claim.Value) && !await session.Advanced.ExistsAsync(claim.Value),
    };

    private static SparkClaims ClaimStore(IAsyncDocumentSession session)
        => new(session.Advanced.DocumentStore, database: session.Advanced.DocumentStore.Database);

    /// <summary>Reserves <paramref name="entity"/>'s client id for its document, or throws when another application holds it.</summary>
    public static async Task ReserveAsync(IAsyncDocumentSession session, OidcApplication entity)
    {
        if (string.IsNullOrEmpty(entity.Id))
            return; // An edit of an application stored before reservations existed and never loaded with an id; nothing to name.

        var kind = Kind(session);
        var owner = entity.Id;
        // A takeover of an abandoned reservation is guarded by the index it read, so nobody else took it meanwhile.
        var result = await ClaimStore(session).ReserveAsync(kind, kind.Key(entity.ClientId.ToLowerInvariant()), owner, _ => owner);
        if (!result.Holds)
            throw Taken(entity);
    }

    /// <summary>Releases the reservation of a deleted application.</summary>
    public static async Task ReleaseAsync(IAsyncDocumentSession session, OidcApplication entity)
    {
        if (string.IsNullOrEmpty(entity.Id))
            return;
        var kind = Kind(session);
        await ClaimStore(session).ReleaseAsync(kind, kind.Key(entity.ClientId.ToLowerInvariant()), entity.Id);
    }

    private static SparkValidationException Taken(OidcApplication entity) => new(
        $"Client id '{entity.ClientId}' is already registered. Client ids must be unique — "
      + "the lookup that resolves them returns whichever document is found first.",
        nameof(OidcApplication.ClientId));
}
