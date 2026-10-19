using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions.Live;
using MintPlayer.Spark.Commerce.Inventory;
using MintPlayer.Spark.Commerce.Ticketing.Venues.Models;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Commerce.Ticketing.Venues.Live;

/// <summary>
/// Pushes the seat states of a live snapshot (<see cref="ISparkVenues.SetLiveAsync"/>) on
/// <c>ticketing-venues:{snapshotId}</c> after an inventory commit touched its seats or block quotas (X2: retained,
/// latest wins, so a buyer who opens the map later gets the last frame at once). Driven by Inventory's commit hook
/// (<see cref="IInventoryCommitted"/>), not by polling.
/// </summary>
/// <remarks>
/// <para>
/// Commits on one snapshot are coalesced: the first one schedules a push after <c>PushDelay</c>, later ones within that
/// window ride along, and a commit landing while a frame is computed schedules the next push. So a hot sale costs at
/// most one frame per snapshot per <c>PushDelay</c>, however many claims commit.
/// </para>
/// <para>
/// Topics are in-process: each instance pushes the commits it made itself. A seat whose hold lapses changes state
/// without a commit until the inventory sweeper releases it, and that release is a commit, so it is pushed too.
/// </para>
/// </remarks>
internal sealed class SeatStatePusher(
    IDocumentStore store,
    VenueSeatMaps maps,
    ISparkLiveTopics topics,
    IOptionsMonitor<SparkTicketingVenuesOptions> options,
    TimeProvider clock,
    ILogger<SeatStatePusher> logger)
{
    private const string SnapshotPrefix = "VenueLayoutSnapshots/";

    private readonly ConcurrentDictionary<string, byte> pending = new(StringComparer.Ordinal);

    /// <summary>Which frame each topic last carried, so an unchanged one is not republished.</summary>
    private readonly ConcurrentDictionary<string, string> last = new(StringComparer.Ordinal);

    /// <summary>The snapshots of units (<c>{snapshotId}/{seatKey}</c>) and block quotas (<c>{snapshotId}/blocks/{key}</c>); other ids are ignored.</summary>
    internal static IEnumerable<string> SnapshotsOf(IEnumerable<string> ids)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in ids)
        {
            if (!id.StartsWith(SnapshotPrefix, StringComparison.Ordinal))
                continue;
            var end = id.IndexOf('/', SnapshotPrefix.Length);
            if (end <= SnapshotPrefix.Length)
                continue;
            var snapshotId = id[..end];
            if (seen.Add(snapshotId))
                yield return snapshotId;
        }
    }

    /// <summary>Schedules a push for each snapshot, unless one is already pending. Returns at once.</summary>
    public void Touch(IEnumerable<string> snapshotIds)
    {
        foreach (var snapshotId in snapshotIds)
            if (pending.TryAdd(snapshotId, 0))
                _ = Task.Run(() => PushLaterAsync(snapshotId));
    }

    private async Task PushLaterAsync(string snapshotId)
    {
        try
        {
            var delay = options.CurrentValue.PushDelay;
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, clock);
        }
        finally
        {
            // Cleared before computing: a commit from here on schedules the next push.
            pending.TryRemove(snapshotId, out _);
        }
        await PushAsync(snapshotId, CancellationToken.None);
    }

    /// <summary>Computes the snapshot's frame and publishes it when the snapshot is live and the frame changed. Never throws.</summary>
    internal async Task<bool> PushAsync(string snapshotId, CancellationToken cancellationToken)
    {
        try
        {
            using (var session = store.OpenAsyncSession())
            {
                var live = await session.LoadAsync<VenueSnapshotLive>(VenueSnapshotLive.For(snapshotId), cancellationToken);
                if (live is null || live.LiveUntilUtc <= clock.GetUtcNow().UtcDateTime)
                    return false;
            }
            var frame = await maps.GetStateAsync(snapshotId, fresh: true, cancellationToken);
            if (frame is null)
                return false;
            var fingerprint = VenueSeatMaps.Fingerprint(frame);
            if (last.TryGetValue(snapshotId, out var previous) && previous == fingerprint)
                return false;
            topics.Publish(VenueSeatMaps.Topic(snapshotId), frame);
            last[snapshotId] = fingerprint;
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Pushing the seat states of snapshot {SnapshotId} failed; its topic stays stale until the next commit.", snapshotId);
            return false;
        }
    }
}

/// <summary>Inventory's commit hook: hands the snapshots a commit touched to <see cref="SeatStatePusher"/>.</summary>
internal sealed class VenueSeatStateCommitted(SeatStatePusher pusher) : IInventoryCommitted
{
    public Task OnCommittedAsync(InventoryCommit commit, CancellationToken cancellationToken)
    {
        pusher.Touch(SeatStatePusher.SnapshotsOf(commit.Units.Concat(commit.Quotas)));
        return Task.CompletedTask;
    }
}
