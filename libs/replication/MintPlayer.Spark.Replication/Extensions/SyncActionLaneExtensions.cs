using MintPlayer.Spark.Messaging;
using MintPlayer.Spark.Messaging.Abstractions;
using MintPlayer.Spark.Replication.Messages;

namespace MintPlayer.Spark.Replication.Extensions;

internal static class SyncActionLaneExtensions
{
    /// <summary>
    /// Declares the lane replication delivers on: ordered per document, so writes to one document
    /// reach the owner module in the order they were made.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why per document rather than per collection.</b> Two updates to one car must not arrive out
    /// of order, or the owner ends up with the earlier value. Two updates to <i>different</i> cars
    /// have no relationship at all, and ordering them would mean one unreachable module could stall
    /// replication for every other document in the collection.
    /// </para>
    /// <para>
    /// <b>Inserts have no document id yet</b>, so they fall back to the collection as their key. That
    /// over-orders — inserts into one collection are serialized — which is the safe direction: an
    /// insert has nothing before it to be ordered against, and serializing costs throughput rather
    /// than correctness. Keying them uniquely would need something the payload does not carry, and a
    /// selector must be pure over the payload because its answer is persisted and never recomputed.
    /// </para>
    /// <para>
    /// The retry schedule is deliberately longer than the messaging default: the failure being waited
    /// out is another module being unreachable, which is measured in minutes rather than seconds. The
    /// block is per document, so a module that is down delays only the documents addressed to it.
    /// </para>
    /// </remarks>
    public static IServiceCollection DeclareSyncActionLane(this IServiceCollection services)
    {
        services.TryDeclareFrameworkLane(SyncActionMessage.LaneName, lane => lane
            .Ordered()
            .PartitionBy<SyncActionMessage>(m =>
                string.IsNullOrEmpty(m.DocumentId) ? m.Collection : m.DocumentId)
            .MaxPartitionsInFlight(8)
            .Retry(RetrySchedule.Ladder("5s 30s 2m 5m"))
            // ~7m35s per document, past the 15-minute default ceiling only if someone lengthens the
            // ladder; stated explicitly so the intent survives a future edit.
            .AcceptPartitionBlock(TimeSpan.FromMinutes(10)));

        return services;
    }
}
