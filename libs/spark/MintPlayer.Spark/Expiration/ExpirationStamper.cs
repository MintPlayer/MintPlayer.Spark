using System.Collections.Concurrent;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Services;
using Raven.Client;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Expiration;

/// <summary>
/// Writes <c>@expires</c> for every <see cref="IExpiring"/> entity on every store, from a store-level
/// <c>OnBeforeStore</c> (<c>docs/seo_analyzer_PRD.md</c> X1).
/// </summary>
/// <remarks>
/// <para>
/// Store-level, so it covers what a persistent-object interceptor cannot: a creation (an
/// <c>IBeforeSave</c> sees no id and cannot reach the metadata yet) and every write outside the
/// pipeline — a message handler or a cron job on a raw session. Patches and bulk inserts raise no
/// event; code using them sets <c>@expires</c> itself.
/// </para>
/// <para>
/// A <b>new</b> document stored with no <c>ExpiresAtUtc</c> gets <c>now + expiresAfter</c> when its
/// model type declares one; after that the property alone decides, so code can extend, shorten or
/// clear it. "New" is a document the session has no change vector for.
/// </para>
/// </remarks>
internal static class ExpirationStamper
{
    public static void Install(IDocumentStore store, IModelLoader modelLoader, TimeProvider time)
    {
        if (store is not DocumentStoreBase storeBase)
            return;

        // Per CLR type: the model's expiresAfter, or null. The model is fixed for the process (a
        // translations reload rebuilds labels, not structure), so caching is safe.
        var defaults = new ConcurrentDictionary<Type, TimeSpan?>();

        storeBase.OnBeforeStore += (_, e) =>
        {
            if (e.Entity is not IExpiring expiring)
                return;

            var metadata = e.DocumentMetadata;
            if (expiring.ExpiresAtUtc is null
                && !metadata.ContainsKey(Constants.Documents.Metadata.ChangeVector)
                && defaults.GetOrAdd(e.Entity.GetType(), type => modelLoader.GetEntityTypeByClrType(type.FullName ?? type.Name)?.ExpiresAfter) is { } after)
            {
                expiring.ExpiresAtUtc = time.GetUtcNow().UtcDateTime + after;
            }

            Stamp(metadata, expiring);
        };
    }

    /// <summary>Mirrors <see cref="IExpiring.ExpiresAtUtc"/> into <c>@expires</c>, normalised to UTC.</summary>
    internal static void Stamp(IMetadataDictionary metadata, IExpiring expiring)
    {
        if (expiring.ExpiresAtUtc is { } expires)
        {
            var utc = SparkExpiration.ToUtc(expires);
            expiring.ExpiresAtUtc = utc;
            metadata[Constants.Documents.Metadata.Expires] = utc.ToString("O");
        }
        else if (metadata.ContainsKey(Constants.Documents.Metadata.Expires))
        {
            metadata.Remove(Constants.Documents.Metadata.Expires);
        }
    }
}
