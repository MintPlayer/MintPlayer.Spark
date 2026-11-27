using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Storage;
using MintPlayer.Spark.IdentityProvider.Models;

namespace MintPlayer.Spark.IdentityProvider.Services;

/// <summary>
/// "Seen once" markers for single-use JWT ids: client assertions (RFC 7523), DPoP proofs (RFC 9449)
/// and request objects (RFC 9101). Each is an <see cref="OidcToken"/> of type
/// <see cref="OidcTokenTypes.DpopProof"/> whose id is the hash of the key, kept until the JWT itself
/// would have expired.
/// </summary>
internal static class OidcReplayCache
{
    /// <summary>
    /// Records <paramref name="key"/> as used. Returns false when it already was. The write is a
    /// create-only store (empty expected version), so two concurrent uses cannot both succeed.
    /// </summary>
    public static async Task<bool> TryUseAsync(ISparkStore store, string key, DateTime expiresAt, CancellationToken ct)
    {
        var id = OidcTokenReference.DocumentId("replay:" + key);
        using var session = store.OpenSession();
        var marker = new OidcToken
        {
            Id = Id<OidcToken>.FromTrusted(id),
            Type = OidcTokenTypes.DpopProof,
            Status = "redeemed",
            CreatedAt = DateTime.UtcNow,
            // A JWT without exp is refused by the callers; never keep a marker for less than a minute.
            ExpiresAt = expiresAt > DateTime.UtcNow.AddMinutes(1) ? expiresAt : DateTime.UtcNow.AddMinutes(1),
        };
        await session.StoreAsync(marker, expectedVersion: string.Empty, id, ct);
        OidcExpiry.Stamp(session, marker, marker.ExpiresAt);
        try
        {
            await session.SaveChangesAsync(ct);
            return true;
        }
        catch (SparkStoreConcurrencyException)
        {
            return false;
        }
    }
}
