using Microsoft.Extensions.Options;
using MintPlayer.Spark.Messaging;
using MintPlayer.Spark.Messaging.Abstractions;
using MintPlayer.Spark.Replication.Abstractions.Configuration;
using MintPlayer.Spark.Replication.Messages;

namespace MintPlayer.Spark.Replication.Extensions;

internal static class SyncActionLaneExtensions
{
    /// <summary>
    /// Declares the lane replication delivers on: ordered per document, so writes to one document
    /// reach the owner module in the order they were made.
    /// </summary>
    /// <remarks>
    /// An ordinary service registration, which is what makes it order-independent — replication may
    /// be added before or after messaging. It previously reached into the
    /// <see cref="IServiceCollection"/> for an already-constructed registry, which worked in one
    /// order and silently did nothing in the other.
    /// </remarks>
    public static IServiceCollection DeclareSyncActionLane(this IServiceCollection services)
        => services.AddSparkLane<SyncActionLaneConfigurator>();
}

/// <summary>
/// Configures the <c>spark-sync</c> lane, reading its concurrency from replication's own options.
/// </summary>
/// <remarks>
/// <para>
/// A DI-constructed configurator rather than a delegate, so the values come from
/// <see cref="SparkReplicationOptions"/> instead of being literals compiled into the registration.
/// That is the capability the eager design could not offer at all: lane configuration ran while the
/// service collection was still being assembled, before options existed.
/// </para>
/// <para>
/// <b>Why per document rather than per collection.</b> Two updates to one car must not arrive out of
/// order, or the owner keeps the earlier value. Two updates to <i>different</i> cars have no
/// relationship, and ordering them would let one unreachable module stall replication for every
/// document in the collection.
/// </para>
/// <para>
/// <b>Inserts have no document id yet</b>, so they fall back to the collection as their key. That
/// over-orders — inserts into one collection are serialized — which is the safe direction: an insert
/// has nothing before it to be ordered against, and serializing costs throughput rather than
/// correctness. Keying them uniquely would need something the payload does not carry, and a selector
/// must be pure over the payload because its answer is persisted and never recomputed.
/// </para>
/// </remarks>
internal sealed class SyncActionLaneConfigurator(IOptions<SparkReplicationOptions> options) : ILaneConfigurator
{
    public void Configure(ILaneBuilder lanes) => lanes
        .Queue<SyncActionMessage>()
        .Ordered()
        .PartitionBy<SyncActionMessage>(m => string.IsNullOrEmpty(m.DocumentId) ? m.Collection : m.DocumentId)
        .MaxPartitionsInFlight(options.Value.SyncMaxDocumentsInFlight)
        // Longer than the messaging default on purpose: the failure being waited out is another
        // module being unreachable, which is measured in minutes rather than seconds. The block is
        // per document, so a module that is down delays only the documents addressed to it.
        .Retry(RetrySchedule.Ladder(options.Value.SyncRetryLadder))
        // ~7m35s per document with the default ladder. Stated explicitly so a future edit to the
        // ladder fails startup loudly rather than silently lengthening a partition's downtime.
        .AcceptPartitionBlock(TimeSpan.FromMinutes(10));
}
