using System.Security.Cryptography;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.IdentityProvider.Models;
using Raven.Client.Documents.Operations.CompareExchange;
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
public static class OidcClientIdReservation
{
    private const string KeyPrefix = "oidc/client-ids/";

    /// <summary>A new client id: 22 url-safe characters, 128 bits of randomness.</summary>
    internal static string GenerateClientId()
        => Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    internal static string Key(string clientId) => KeyPrefix + clientId.ToLowerInvariant();

    /// <summary>
    /// Stores a new application in <paramref name="session"/> together with its client-id reservation, or throws
    /// <see cref="SparkValidationException"/> when another application holds the client id. Does not save: the
    /// caller commits. The way for code outside the PersistentObject screens and the registration endpoints (a
    /// seeder, a migration, a test host) to add an application; a bare <c>StoreAsync</c> leaves the client id
    /// unreserved, so a later registration that names it (a Client ID Metadata Document) is free to take it.
    /// </summary>
    public static async Task StoreNewAsync(IAsyncDocumentSession session, OidcApplication application, CancellationToken ct = default)
    {
        // Stored first so the session assigns the document id the reservation names.
        await session.StoreAsync(application, ct);
        try
        {
            await ReserveAsync(session, application);
        }
        catch
        {
            // Refused: a caller that catches and saves anyway must not commit the duplicate.
            session.Advanced.Evict(application);
            throw;
        }
    }

    /// <summary>Reserves <paramref name="entity"/>'s client id for its document, or throws when another application holds it.</summary>
    internal static async Task ReserveAsync(IAsyncDocumentSession session, OidcApplication entity)
    {
        if (string.IsNullOrEmpty(entity.Id))
            return; // An edit of an application stored before reservations existed and never loaded with an id; nothing to name.

        var operations = session.Advanced.DocumentStore.Operations.ForDatabase(session.Advanced.DocumentStore.Database);
        var key = Key(entity.ClientId);

        // Two attempts: the second only after taking over a reservation whose owner is gone.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var existing = await operations.SendAsync(new GetCompareExchangeValueOperation<string>(key));
            if (existing is null)
            {
                var created = await operations.SendAsync(new PutCompareExchangeValueOperation<string>(key, entity.Id, 0));
                if (created.Successful || string.Equals(created.Value, entity.Id, StringComparison.OrdinalIgnoreCase))
                    return;
                continue;
            }

            if (string.Equals(existing.Value, entity.Id, StringComparison.OrdinalIgnoreCase))
                return;

            if (await session.Advanced.ExistsAsync(existing.Value))
                throw Taken(entity);

            // The owner was never saved, or was deleted without releasing: take it over, but only
            // if nobody else did in the meantime (the index is the compare).
            var takenOver = await operations.SendAsync(new PutCompareExchangeValueOperation<string>(key, entity.Id, existing.Index));
            if (takenOver.Successful)
                return;
        }

        throw Taken(entity);
    }

    /// <summary>Releases the reservation of a deleted application.</summary>
    internal static async Task ReleaseAsync(IAsyncDocumentSession session, OidcApplication entity)
    {
        var operations = session.Advanced.DocumentStore.Operations.ForDatabase(session.Advanced.DocumentStore.Database);
        var existing = await operations.SendAsync(new GetCompareExchangeValueOperation<string>(Key(entity.ClientId)));
        if (existing is not null && string.Equals(existing.Value, entity.Id, StringComparison.OrdinalIgnoreCase))
            await operations.SendAsync(new DeleteCompareExchangeValueOperation<string>(Key(entity.ClientId), existing.Index));
    }

    private static SparkValidationException Taken(OidcApplication entity) => new(
        $"Client id '{entity.ClientId}' is already registered. Client ids must be unique — "
      + "the lookup that resolves them returns whichever document is found first.",
        nameof(OidcApplication.ClientId));
}
