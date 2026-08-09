using MintPlayer.Spark.Abstractions;
using Raven.Client.Documents.Conventions;

namespace MintPlayer.Spark;

/// <summary>
/// The document-id rules every Spark store follows.
/// <para>
/// Exposed rather than inlined into the store setup so that a test — or an app that opens its own
/// <c>DocumentStore</c> against a second database — exercises the same rules the framework applies,
/// instead of a hand-written copy that can drift from them.
/// </para>
/// </summary>
public static class SparkDocumentStoreConventions
{
    /// <summary>
    /// An entity implementing <see cref="IHasNaturalId"/> is stored under the id it derives;
    /// everything else gets <c>{Collection}/{Guid}</c>.
    /// <para>
    /// The two are not alternatives to choose between: RavenDB consults registered id conventions
    /// first and only falls back to <see cref="DocumentConventions.AsyncDocumentIdGenerator"/> when
    /// none matches, so both are installed and the entity decides.
    /// </para>
    /// </summary>
    public static DocumentConventions ApplySparkIdConventions(this DocumentConventions conventions)
    {
        conventions.RegisterAsyncIdConvention<IHasNaturalId>(
            (_, entity) => Task.FromResult(entity.GetId()));

        // GUIDs rather than HiLo: HiLo reserves ranges from the server, which serializes writes
        // behind a cluster-wide operation and makes ids guessable in sequence.
        conventions.AsyncDocumentIdGenerator = (_, entity) =>
            Task.FromResult($"{conventions.GetCollectionName(entity.GetType())}/{Guid.NewGuid()}");

        return conventions;
    }
}
