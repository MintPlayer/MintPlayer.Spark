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
/// <para>
/// Both must be installed before <c>DocumentStore.Initialize()</c>: RavenDB freezes conventions
/// there and throws on any later change.
/// </para>
/// </summary>
public static class SparkDocumentStoreConventions
{
    /// <summary>
    /// Stores an entity implementing <see cref="IHasNaturalId"/> under the id it derives from its
    /// own contents, rather than a generated one.
    /// </summary>
    public static DocumentConventions UseNaturalIds(this DocumentConventions conventions)
    {
        conventions.RegisterAsyncIdConvention<IHasNaturalId>(
            (_, entity) => Task.FromResult(entity.GetId()));

        return conventions;
    }

    /// <summary>
    /// Gives every other entity <c>{Collection}/{Guid}</c>.
    /// <para>
    /// GUIDs rather than HiLo: HiLo reserves ranges from the server, which serializes writes behind
    /// a cluster-wide operation and makes ids guessable in sequence.
    /// </para>
    /// <para>
    /// This is a fallback, not an alternative to <see cref="UseNaturalIds"/>. RavenDB consults
    /// registered id conventions first and reaches this generator only when none matches, so
    /// installing both is what lets the entity decide — the order they are installed in does not
    /// matter.
    /// </para>
    /// </summary>
    public static DocumentConventions UseGeneratedIds(this DocumentConventions conventions)
    {
        conventions.AsyncDocumentIdGenerator = (_, entity) =>
            Task.FromResult($"{conventions.GetCollectionName(entity.GetType())}/{Guid.NewGuid()}");

        return conventions;
    }
}
