using System.Security.Cryptography;
using System.Text;

namespace MintPlayer.Spark.IdentityProvider.Services;

/// <summary>
/// The storage rule for every opaque value this package hands to a browser or a client:
/// the value itself is never persisted, and the document recording it is keyed by the
/// value's SHA-256.
/// <para>
/// That buys two things which the obvious alternative — storing the raw value in a field
/// and finding it through a static index — does not:
/// </para>
/// <list type="number">
/// <item>
/// <b>Lookups become strongly consistent.</b> RavenDB index queries are eventually
/// consistent, so a single-use value consumed moments earlier could still be returned as
/// <c>Status == "valid"</c> until the index caught up, and replayed inside that window.
/// A point-load by id has no such window.
/// </item>
/// <item>
/// <b>A database leak yields no usable credentials.</b> Only the hash is at rest, so a dump
/// of the collection cannot be replayed against the endpoint that accepts it.
/// </item>
/// </list>
/// <para>
/// Values are high-entropy and generated here, so a plain SHA-256 suffices — unlike a client
/// secret (see <see cref="ClientSecretHasher"/>), which may be operator-chosen and therefore
/// needs a salt and a work factor. Uniqueness holds by construction: two distinct values
/// cannot claim the same document.
/// </para>
/// <para>
/// Callers do not use this type directly; they go through a named facade
/// (<see cref="OidcTokenReference"/>, <see cref="OidcRequestReference"/>) so the collection a
/// handle belongs to is chosen once, at the facade, and never passed around as a parameter.
/// </para>
/// </summary>
internal static class OpaqueHandle
{
    /// <summary>The id of the document in <paramref name="collectionPrefix"/> recording <paramref name="value"/>.</summary>
    public static string DocumentId(string collectionPrefix, string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);
        return collectionPrefix + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    /// <summary>
    /// A new handle: 256 bits of cryptographic randomness, urlsafe. Returned to its holder
    /// once; only <see cref="DocumentId"/> of it is ever persisted.
    /// </summary>
    public static string Generate()
        => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
