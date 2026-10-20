using MintPlayer.Spark.Commerce.Promotions;
using MintPlayer.Spark.WaitingRoom;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;

namespace MintPlayer.Spark.Commerce.WaitingRoom;

/// <summary>
/// Promotions' <see cref="IWaitingListQueueState"/> over the waiting room (§4.6: a ticket waiting list activates only
/// after the sale's queue closed). A room's queue has closed when it is sold out for good or closed (by status, or because
/// its end passed). "May return" (sold out while holds are outstanding) is not closed: places can still come back.
/// </summary>
/// <remarks>
/// A ticketing phase's room is loaded by its key (no index, so a room created a moment ago is seen). For a
/// <c>waiting-room:{gate}</c> scope every room bound to the gate must have closed, and a gate without any room has no
/// queue to wait for.
/// </remarks>
internal sealed class WaitingListQueueState(ISparkWaitingRooms rooms, IDocumentStore store, TimeProvider time) : IWaitingListQueueState
{
    public bool Handles(string scope) => CommerceWaitingRoomScopes.TryGetGate(scope, out _);

    public async Task<bool> IsQueueClosedAsync(string scope, CancellationToken cancellationToken)
    {
        if (!CommerceWaitingRoomScopes.TryGetGate(scope, out var gate))
            return false;
        var now = time.GetUtcNow();

        if (scope.StartsWith(CommerceWaitingRoomScopes.TicketingPhasePrefix, StringComparison.Ordinal))
        {
            // A queued phase without its room yet (not saved since the queue was switched on) is not closed.
            return await rooms.GetRoomAsync(CommerceWaitingRoomScopes.RoomKey(gate), cancellationToken) is { } found
                && IsClosed(found.Room, found.State, now);
        }

        using var session = store.OpenAsyncSession();
        var bound = await session.Query<WaitingRoomDefinition>()
            .Customize(c => c.WaitForNonStaleResults(TimeSpan.FromSeconds(10)))
            .Where(r => r.Gate == gate)
            .Take(64)
            .ToListAsync(cancellationToken);
        foreach (var room in bound)
        {
            var state = await session.LoadAsync<WaitingRoomState>(room.Id + "/state", cancellationToken);
            if (!IsClosed(room, state, now))
                return false;
        }
        return true;
    }

    internal static bool IsClosed(WaitingRoomDefinition room, WaitingRoomState? state, DateTimeOffset now)
        => room.EndsAt is { } ends && ends <= now
        || state?.Status is WaitingRoomStatus.SoldOut or WaitingRoomStatus.Closed;
}
