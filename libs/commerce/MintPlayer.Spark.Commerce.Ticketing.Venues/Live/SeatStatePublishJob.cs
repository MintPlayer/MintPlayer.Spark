using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions.Live;
using MintPlayer.Spark.Commerce.Ticketing.Venues.Models;
using MintPlayer.Spark.Cron;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Commerce.Ticketing.Venues.Live;

/// <summary>Which frame each live topic last carried, so an unchanged one is not republished. In-process, like the topics.</summary>
internal sealed class SeatStatePublishState
{
    public ConcurrentDictionary<string, string> Last { get; } = new(StringComparer.Ordinal);
}

/// <summary>
/// Publishes the seat states of every live snapshot (<see cref="ISparkVenues.SetLiveAsync"/>) on
/// <c>ticketing-venues:{snapshotId}</c> when they changed (X2: retained, latest wins, so a buyer who opens the map
/// later gets the last frame at once). Runs every few seconds (<c>LiveSchedule</c>). Topics are in-process: on several
/// instances each one publishes for its own sockets, which this job does on every node it runs on.
/// </summary>
internal sealed class SeatStatePublishJob(
    IDocumentStore store,
    VenueSeatMaps maps,
    ISparkLiveTopics topics,
    SeatStatePublishState state,
    IOptions<SparkTicketingVenuesOptions> options,
    TimeProvider clock,
    ILogger<SeatStatePublishJob> logger) : ISparkCronJob
{
    /// <summary>The shipped default; <c>Spark:Commerce:Ticketing:Venues:LiveSchedule</c> overrides it.</summary>
    public static string CronSchedule => "*/3 * * * * *";

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        List<string> live;
        using (var session = store.OpenAsyncSession())
        {
            var now = clock.GetUtcNow().UtcDateTime;
            live = await session.Query<VenueSnapshotLive>()
                .Where(l => l.LiveUntilUtc > now)
                .OrderBy(l => l.LiveUntilUtc)
                .Take(options.Value.MaxLiveSnapshots)
                .Select(l => l.SnapshotId)
                .ToListAsync(cancellationToken);
        }

        foreach (var snapshotId in live)
        {
            try
            {
                var frame = await maps.GetStateAsync(snapshotId, fresh: true, cancellationToken);
                if (frame is null)
                    continue;
                var fingerprint = VenueSeatMaps.Fingerprint(frame);
                if (state.Last.TryGetValue(snapshotId, out var previous) && previous == fingerprint)
                    continue;
                topics.Publish(VenueSeatMaps.Topic(snapshotId), frame);
                state.Last[snapshotId] = fingerprint;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Publishing the seat states of snapshot {SnapshotId} failed; its topic stays stale until the next run.", snapshotId);
            }
        }
    }
}
