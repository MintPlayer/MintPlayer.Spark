using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions.Live;
using MintPlayer.Spark.Commerce.Inventory;
using MintPlayer.Spark.Commerce.Ticketing.Venues;
using MintPlayer.Spark.Commerce.Ticketing.Venues.Contracts;
using MintPlayer.Spark.Commerce.Ticketing.Venues.Live;
using MintPlayer.Spark.Commerce.Ticketing.Venues.Models;
using MintPlayer.Spark.Commerce.Ticketing.Venues.Plans;
using MintPlayer.Spark.Commerce.Ticketing.Venues.Services;
using MintPlayer.Spark.Testing;

namespace MintPlayer.Spark.Commerce.Tests.Venues;

/// <summary>
/// Item 2 (round 6): a live snapshot's seat states are pushed after the inventory commits that touched its seats
/// (Inventory's commit hook), instead of polled every 3 seconds.
/// </summary>
public class SeatStatePushTests : SparkTestDriver
{
    private readonly SparkTicketingVenuesOptions options = new() { PushDelay = TimeSpan.Zero };
    private readonly RecordingTopics topics = new();

    [Fact]
    public void Commits_map_to_the_snapshots_of_their_units_and_block_quotas()
    {
        SeatStatePusher.SnapshotsOf([
            "VenueLayoutSnapshots/abc/A-1-1",
            "VenueLayoutSnapshots/abc/blocks/P",
            "VenueLayoutSnapshots/def/B-2-7",
            "quotas/other",
            "VenueLayoutSnapshots/",
        ]).Should().Equal("VenueLayoutSnapshots/abc", "VenueLayoutSnapshots/def");
    }

    [Fact]
    public async Task A_claim_on_a_live_snapshot_pushes_a_new_frame_and_one_on_a_snapshot_that_is_not_live_does_not()
    {
        SeatStatePusher? pusher = null;
        var engine = new InventoryEngine(Store, committed: (commit, ct) => new VenueSeatStateCommitted(pusher!).OnCommittedAsync(commit, ct));
        var maps = new VenueSeatMaps(Store, engine, new FixedMonitor(options), TimeProvider.System, NullLogger<VenueSeatMaps>.Instance);
        pusher = new SeatStatePusher(Store, maps, topics, new FixedMonitor(options), TimeProvider.System, NullLogger<SeatStatePusher>.Instance);

        var live = await SnapshotAsync(engine, "EventDates/1-A", liveFor: TimeSpan.FromHours(1));
        var dark = await SnapshotAsync(engine, "EventDates/2-A", liveFor: null);

        // The first frame (as SetLiveAsync pushes it).
        (await pusher.PushAsync(live.SnapshotId, CancellationToken.None)).Should().BeTrue();
        var baseline = topics.Frames(live.SnapshotId).Last();

        var held = await engine.TryClaimAsync(new ClaimRequest("claims/p1", "baskets/p1", DateTimeOffset.UtcNow.AddMinutes(10), [], Units: [live.UnitId("A-1-3")]));
        held.Succeeded.Should().BeTrue();

        // Pushed by the commit hook, in the background: wait (bounded) for a frame that differs from the baseline.
        var pushed = await topics.WaitForAsync(live.SnapshotId, f => f.States != baseline.States, TimeSpan.FromSeconds(10));
        pushed.Should().NotBeNull();

        (await engine.TryClaimAsync(new ClaimRequest("claims/p2", "baskets/p2", DateTimeOffset.UtcNow.AddMinutes(10), [], Units: [dark.UnitId("A-1-3")]))).Succeeded.Should().BeTrue();
        (await pusher.PushAsync(dark.SnapshotId, CancellationToken.None)).Should().BeFalse();
        topics.Frames(dark.SnapshotId).Should().BeEmpty();
    }

    private async Task<SnapshotInventory> SnapshotAsync(InventoryEngine engine, string scope, TimeSpan? liveFor)
    {
        var id = SparkVenues.SnapshotId(scope);
        var plan = LayoutPlanTests.Plan();
        using var session = Store.OpenAsyncSession();
        await session.StoreAsync(new VenueLayoutSnapshot { Id = id, Scope = scope, VenueName = "Hall", Plan = plan, CreatedAtUtc = DateTime.UtcNow }, id);
        if (liveFor is { } duration)
            await session.StoreAsync(new VenueSnapshotLive { SnapshotId = id, LiveUntilUtc = DateTime.UtcNow + duration, Provisioned = true }, VenueSnapshotLive.For(id));
        await session.SaveChangesAsync();
        var map = new SnapshotInventory(id, plan, options.ExcludedFromChains);
        await map.ProvisionAsync(engine, options.ShardsPerBlockQuota, CancellationToken.None);
        return map;
    }

    private sealed class RecordingTopics : ISparkLiveTopics
    {
        private readonly ConcurrentQueue<(string Topic, SeatStateFrame Frame)> published = new();
        private readonly SemaphoreSlim signal = new(0);

        public void Publish(string topic, object payload)
        {
            if (payload is SeatStateFrame frame)
            {
                published.Enqueue((topic, frame));
                signal.Release();
            }
        }

        public void PublishPrivate(string identityKey, string topic, object payload) { }

        public IReadOnlyList<SeatStateFrame> Frames(string snapshotId)
            => published.Where(p => p.Topic == VenueSeatMaps.Topic(snapshotId)).Select(p => p.Frame).ToList();

        /// <summary>The first frame on the snapshot's topic that matches, or null when none arrives within <paramref name="bound"/> (a failure bound).</summary>
        public async Task<SeatStateFrame?> WaitForAsync(string snapshotId, Func<SeatStateFrame, bool> match, TimeSpan bound)
        {
            var deadline = DateTime.UtcNow + bound;
            while (true)
            {
                if (Frames(snapshotId).FirstOrDefault(match) is { } found)
                    return found;
                var left = deadline - DateTime.UtcNow;
                if (left <= TimeSpan.Zero || !await signal.WaitAsync(left))
                    return Frames(snapshotId).FirstOrDefault(match);
            }
        }
    }

    private sealed class FixedMonitor(SparkTicketingVenuesOptions value) : IOptionsMonitor<SparkTicketingVenuesOptions>
    {
        public SparkTicketingVenuesOptions CurrentValue => value;
        public SparkTicketingVenuesOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<SparkTicketingVenuesOptions, string?> listener) => null;
    }
}
