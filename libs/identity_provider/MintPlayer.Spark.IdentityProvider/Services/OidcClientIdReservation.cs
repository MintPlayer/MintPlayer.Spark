using System.Security.Cryptography;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Storage;
using MintPlayer.Spark.IdentityProvider.Models;

namespace MintPlayer.Spark.IdentityProvider.Services;

/// <summary>
/// Makes a client id unique atomically (O17, <c>docs/identity_provider_platform_PRD.md</c> D1): a
/// reservation <c>oidc/client-ids/&lt;client id&gt;</c> naming the application document that owns it
/// (<see cref="ISparkSession.Reserve"/>; on RavenDB the same compare-exchange value the hand-written code wrote).
/// <para>
/// The save claims it with the application. A key whose owner no longer exists is taken over, so a
/// failed save or a delete without release never burns a client id.
/// </para>
/// <para>
/// Public because the reservation is the only way the provider finds an application by its client id (#536 D47): code
/// that stores an application outside the provider's own pipeline (an application's seed migration) reserves it here.
/// </para>
/// </summary>
public static class OidcClientIdReservation
{
    private const string KeyPrefix = "oidc/client-ids/";

    /// <summary>A new client id: 22 url-safe characters, 128 bits of randomness.</summary>
    public static string GenerateClientId()
        => Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string Key(string clientId) => KeyPrefix + clientId.ToLowerInvariant();

    /// <summary>
    /// Reserves <paramref name="entity"/>'s client id for its document with the session's next save
    /// (<see cref="ISparkSession.Reserve"/>, #536 D47), or throws when another existing application holds it.
    /// </summary>
    /// <remarks>
    /// The check reads the holder now, so the caller gets a validation error on <c>ClientId</c>. A save racing another
    /// for the same id fails at commit instead (<see cref="SparkReservationTakenException"/>), and nothing is written.
    /// A key held by a document that no longer exists is taken over by the save.
    /// </remarks>
    public static async Task ReserveAsync(ISparkSession session, OidcApplication entity, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(entity.Id.Value))
            return; // An edit of an application stored before reservations existed and never loaded with an id; nothing to name.

        var key = Key(entity.ClientId);
        var holder = await session.GetReservationAsync(key, ct);
        if (holder is not null
            && !string.Equals(holder, entity.Id.Value, StringComparison.OrdinalIgnoreCase)
            && await session.ExistsAsync(typeof(OidcApplication), holder, ct))
            throw Taken(entity);

        session.Reserve(key, entity.Id.Value);
    }

    /// <summary>Releases the reservation of a deleted application with the session's next save.</summary>
    public static void Release(ISparkSession session, OidcApplication entity)
        => session.Release(Key(entity.ClientId), entity.Id.Value);

    private static SparkValidationException Taken(OidcApplication entity) => new(
        $"Client id '{entity.ClientId}' is already registered. Client ids must be unique — "
      + "the lookup that resolves them returns whichever document is found first.",
        nameof(OidcApplication.ClientId));
}
