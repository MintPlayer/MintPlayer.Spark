using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Commerce.Promotions;

/// <summary>
/// Answers whether a sale's queue has closed (§4.6: the waiting list activates only after the queue closed). The waiting
/// room (<c>MintPlayer.Spark.WaitingRoom</c>, §4.12) knows; Promotions does not reference it, and the waiting room
/// references neither Checkout nor Promotions, so a host (or a bridge package) implements this over
/// <c>ISparkWaitingRooms</c>. Discovered (<c>[SparkDiscoverable]</c>); without one, a list with a
/// <c>QueueScope</c> waits for staff to mark the queue closed.
/// </summary>
[SparkDiscoverable]
public interface IWaitingListQueueState
{
    /// <summary>Whether this source knows <paramref name="scope"/> (a waiting-room scope, e.g. a sale-phase id).</summary>
    bool Handles(string scope);

    /// <summary>Whether the queue of <paramref name="scope"/> has closed (no one is waiting to be admitted any more).</summary>
    Task<bool> IsQueueClosedAsync(string scope, CancellationToken cancellationToken);
}
