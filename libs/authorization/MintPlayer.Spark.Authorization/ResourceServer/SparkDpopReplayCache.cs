using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Raven.Client;
using Raven.Client.Documents;
using Raven.Client.Documents.Operations.CompareExchange;
using Raven.Client.Json;

namespace MintPlayer.Spark.Authorization.ResourceServer;

/// <summary>
/// The DPoP proof ids (<c>jti</c>, RFC 9449 §11.1) a resource server has already accepted, shared by every instance
/// that uses the same database: a proof replayed against a second instance inside its lifetime is refused there too.
/// </summary>
/// <remarks>
/// <para>
/// Each use is a compare-exchange put at index 0, which RavenDB commits through the cluster's consensus: exactly one
/// put of a key succeeds, however many instances (or cluster nodes) race. A create-only document store checks its
/// change vector only on the node that receives it, so two nodes could both accept the same proof.
/// </para>
/// <para>
/// The item carries <c>@expires</c> at the end of the proof's lifetime. The cluster deletes expired compare-exchange
/// items itself (<c>Cluster.CompareExchangeExpiredDeleteFrequencyInSec</c>, <c>CompareExchangeExpirationStorage</c> in
/// the 7.2 server), so this needs no document-expiration setting on the database and leaves nothing behind.
/// </para>
/// <para>
/// The key is a SHA-256 of the proof key's thumbprint and the <c>jti</c>, so a caller-chosen <c>jti</c> never reaches
/// the key verbatim and the key length is fixed.
/// </para>
/// </remarks>
internal sealed class SparkDpopReplayCache(IDocumentStore store)
{
    private const string KeyPrefix = "spark/resource-server/dpop/";

    /// <summary>Records <paramref name="key"/> until <paramref name="expiresAt"/> (UTC). False when it was already recorded.</summary>
    public async Task<bool> TryUseAsync(string key, DateTime expiresAt, CancellationToken cancellationToken)
    {
        var hashed = KeyPrefix + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        var expires = DateTime.SpecifyKind(expiresAt, DateTimeKind.Utc);
        var metadata = new MetadataAsDictionary
        {
            [Constants.Documents.Metadata.Expires] = expires.ToString("O", CultureInfo.InvariantCulture),
        };

        var result = await store.Operations.SendAsync(
            new PutCompareExchangeValueOperation<string>(hashed, "used", 0, metadata), token: cancellationToken);
        return result.Successful;
    }
}
