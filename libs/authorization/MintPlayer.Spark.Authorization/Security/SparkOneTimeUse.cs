using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Raven.Client;
using Raven.Client.Documents;
using Raven.Client.Documents.Operations.CompareExchange;
using Raven.Client.Json;

namespace MintPlayer.Spark.Authorization.Security;

/// <summary>
/// Values that may be accepted once, shared by every instance and every cluster node that uses the same database:
/// DPoP proof ids at a resource server and at the identity provider (RFC 9449 §11.1), client-assertion ids
/// (RFC 7523 §3) and request-object ids (RFC 9101). A value used at one instance is refused at every other one for
/// as long as it is kept.
/// </summary>
/// <remarks>
/// <para>
/// Each use is a compare-exchange put at index 0, which RavenDB commits through the cluster's consensus: exactly one
/// put of a key succeeds, however many instances or nodes race. A create-only document store (empty change vector)
/// is checked only on the node that receives it, so two nodes could both accept the same value; that is what the
/// identity provider's <c>OidcReplayCache</c> did (parity PRD A12).
/// </para>
/// <para>
/// The item carries <c>@expires</c>. The cluster deletes expired compare-exchange items itself
/// (<c>Cluster.CompareExchangeExpiredDeleteFrequencyInSec</c>, <c>CompareExchangeExpirationStorage</c> in the 7.2
/// server), so this needs no document-expiration setting on the database and leaves nothing behind.
/// </para>
/// <para>
/// The key is <c>spark/one-time/{purpose}/</c> plus the SHA-256 of the value, so a caller-chosen <c>jti</c> never
/// reaches the key verbatim and the key length is fixed. The purpose keeps the registries of different callers apart.
/// </para>
/// </remarks>
public static partial class SparkOneTimeUse
{
    private const string KeyPrefix = "spark/one-time/";

    /// <summary>
    /// How long past its own expiry a value is kept: the one-minute clock skew the JWT validators allow, so a token
    /// still accepted a moment after its <c>exp</c> cannot be replayed in that moment.
    /// </summary>
    public static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Records <paramref name="value"/> for <paramref name="purpose"/> until <paramref name="expiresAt"/> (UTC) plus
    /// <see cref="ClockSkew"/>. Returns false when it was already recorded, on this instance or any other.
    /// </summary>
    /// <param name="purpose">A constant naming the registry: lower-case letters, digits and dashes.</param>
    /// <param name="value">The one-time value, untrusted: it is hashed into the key.</param>
    /// <exception cref="ArgumentException"><paramref name="purpose"/> is not a constant-shaped name.</exception>
    public static async Task<bool> TryUseAsync(IDocumentStore store, string purpose, string value, DateTime expiresAt, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var until = DateTime.SpecifyKind(expiresAt, DateTimeKind.Utc);
        var expires = (until > now ? until : now) + ClockSkew;
        var metadata = new MetadataAsDictionary
        {
            [Constants.Documents.Metadata.Expires] = expires.ToString("O", CultureInfo.InvariantCulture),
        };

        var result = await store.Operations.SendAsync(
            new PutCompareExchangeValueOperation<string>(Key(purpose, value), "used", 0, metadata), token: cancellationToken);
        return result.Successful;
    }

    /// <summary>The compare-exchange key that records <paramref name="value"/> for <paramref name="purpose"/>.</summary>
    /// <exception cref="ArgumentException"><paramref name="purpose"/> is not a constant-shaped name.</exception>
    public static string Key(string purpose, string value)
    {
        if (string.IsNullOrEmpty(purpose) || !PurposePattern().IsMatch(purpose))
            throw new ArgumentException("A one-time-use purpose is a constant of lower-case letters, digits and dashes.", nameof(purpose));
        return KeyPrefix + purpose + "/" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    [GeneratedRegex("^[a-z0-9-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex PurposePattern();
}
