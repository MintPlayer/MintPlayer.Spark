using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.WaitingRoom;

/// <summary>
/// What a room can still sell (§4.12 "projected remaining capacity", "Sold out"). The waiting room does not reference
/// the inventory engine; the host (or ticketing) answers from it, e.g. with <c>ISparkInventory.GetAvailabilityAsync</c>.
/// Discovered (<c>[SparkDiscoverable]</c>); the first one that answers non-null for a room's
/// <see cref="WaitingRoomDefinition.CapacityKey"/> is used.
/// </summary>
[SparkDiscoverable]
public interface IWaitingRoomCapacity
{
    /// <summary>The capacity behind <paramref name="capacityKey"/>, or null when this source does not know it.</summary>
    Task<WaitingRoomCapacity?> GetAsync(string capacityKey, CancellationToken cancellationToken);
}

/// <summary>A capacity reading.</summary>
/// <param name="Remaining">Units neither sold nor held now.</param>
/// <param name="OutstandingHolds">Units held and not yet paid: they may come back when holds lapse.</param>
public sealed record WaitingRoomCapacity(long Remaining, long OutstandingHolds);

/// <summary>
/// Told when an entry is called, admitted or lost, after the change committed: a host sends a Web Push notification to a
/// hidden tab's subscription (K11: Push API + Notifications, "queue turn"), or a ballot's personal link by mail.
/// Discovered (<c>[SparkDiscoverable]</c>). At least once: a crash between the commit and the call loses the call.
/// </summary>
[SparkDiscoverable]
public interface IWaitingRoomNotifier
{
    /// <summary><paramref name="kind"/>: <c>called</c>, <c>admitted</c>, <c>missed</c> or <c>lost</c>.</summary>
    Task OnEntryChangedAsync(string kind, WaitingRoomDefinition room, WaitingRoomEntry entry, CancellationToken cancellationToken);
}
