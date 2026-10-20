using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions.Live;
using MintPlayer.Spark.Commerce.Inventory;
using MintPlayer.Spark.Commerce.Ticketing.Venues.Contracts;
using MintPlayer.Spark.Commerce.Ticketing.Venues.Models;
using MintPlayer.Spark.Commerce.Ticketing.Venues.Plans;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Commerce.Ticketing.Venues.Live;

/// <summary>
/// The buyer's seat map of a snapshot (S7): the packed payload (cached, since a snapshot never changes) and the live
/// seat states, both anonymous. States come from the inventory engine (<see cref="ISparkInventory.GetUnitStatesAsync"/>)
/// and are computed at most once per <c>StateCacheDuration</c> per snapshot, however many buyers ask.
/// </summary>
/// <remarks>
/// Inventory names the units and quotas each commit touched (<see cref="IInventoryCommitted"/>), and
/// <see cref="SeatStatePusher"/> pushes the frame of every live snapshot among them, coalesced per snapshot.
/// </remarks>
public sealed class VenueSeatMaps(
    IDocumentStore store,
    ISparkInventory inventory,
    IOptionsMonitor<SparkTicketingVenuesOptions> options,
    TimeProvider clock,
    ILogger<VenueSeatMaps> logger) : IDisposable
{
    public const string TopicPrefix = "ticketing-venues:";

    /// <summary>The engine reads compare-exchange values in batches this large.</summary>
    internal const int StateBatch = 1024;

    private readonly MemoryCache payloads = new(new MemoryCacheOptions { SizeLimit = 64 });
    private readonly ConcurrentDictionary<string, Lazy<Task<SeatStateFrame?>>> computing = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (SeatStateFrame Frame, DateTime At)> states = new(StringComparer.Ordinal);

    public static string Topic(string snapshotId) => TopicPrefix + snapshotId;

    private sealed record Loaded(VenueLayoutSnapshot Snapshot, SnapshotInventory Map, SeatMapPayload Payload, string[] Units);

    /// <summary>The packed map of a snapshot, or null when there is none.</summary>
    public async Task<SeatMapPayload?> GetPayloadAsync(string snapshotId, CancellationToken cancellationToken)
        => (await LoadAsync(snapshotId, cancellationToken))?.Payload;

    /// <summary>The snapshot (immutable, cached with its map).</summary>
    public async Task<VenueLayoutSnapshot?> GetSnapshotAsync(string snapshotId, CancellationToken cancellationToken)
        => (await LoadAsync(snapshotId, cancellationToken))?.Snapshot;

    /// <summary>The current seat states, at most <c>StateCacheDuration</c> old; <paramref name="fresh"/> recomputes.</summary>
    public async Task<SeatStateFrame?> GetStateAsync(string snapshotId, bool fresh, CancellationToken cancellationToken)
    {
        if (!fresh && states.TryGetValue(snapshotId, out var cached) && clock.GetUtcNow().UtcDateTime - cached.At < options.CurrentValue.StateCacheDuration)
            return cached.Frame;

        // One computation per snapshot at a time; concurrent callers share it.
        var lazy = computing.GetOrAdd(snapshotId, id => new Lazy<Task<SeatStateFrame?>>(() => ComputeAsync(id, CancellationToken.None)));
        try
        {
            return await lazy.Value.WaitAsync(cancellationToken);
        }
        finally
        {
            if (lazy.IsValueCreated && lazy.Value.IsCompleted)
                computing.TryRemove(new KeyValuePair<string, Lazy<Task<SeatStateFrame?>>>(snapshotId, lazy));
        }
    }

    private async Task<SeatStateFrame?> ComputeAsync(string snapshotId, CancellationToken cancellationToken)
    {
        var loaded = await LoadAsync(snapshotId, cancellationToken);
        if (loaded is null)
            return null;

        var unitStates = new UnitState[loaded.Units.Length];
        for (var start = 0; start < loaded.Units.Length; start += StateBatch)
        {
            var batch = loaded.Units.AsSpan(start, Math.Min(StateBatch, loaded.Units.Length - start)).ToArray();
            var read = await inventory.GetUnitStatesAsync(batch, cancellationToken);
            for (var i = 0; i < batch.Length; i++)
                unitStates[start + i] = read.TryGetValue(batch[i], out var state) ? state : UnitState.Free;
        }

        var blocks = new List<SeatMapBlockAvailability>();
        foreach (var block in loaded.Payload.Blocks)
        {
            if (block.Kind == BlockKind.SeatedNumbered)
            {
                var free = 0;
                for (var i = block.SeatStart; i < block.SeatStart + block.SeatCount; i++)
                    if (unitStates[i] == UnitState.Free) free++;
                blocks.Add(new(block.Key, free));
                continue;
            }
            int available;
            try
            {
                available = (await inventory.GetAvailabilityAsync(loaded.Map.QuotaId(block.Key), cancellationToken)).Available;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Not provisioned yet: nothing is on sale there.
                logger.LogDebug(ex, "No availability for block {Block} of snapshot {SnapshotId}.", block.Key, snapshotId);
                available = 0;
            }
            blocks.Add(new(block.Key, available));
        }

        var frame = new SeatStateFrame(snapshotId, unitStates.Length, Convert.ToBase64String(SeatMapPacking.PackStates(unitStates)), blocks);
        states[snapshotId] = (frame, clock.GetUtcNow().UtcDateTime);
        return frame;
    }

    private async Task<Loaded?> LoadAsync(string snapshotId, CancellationToken cancellationToken)
    {
        if (payloads.TryGetValue(snapshotId, out Loaded? hit))
            return hit;
        if (!snapshotId.StartsWith("VenueLayoutSnapshots/", StringComparison.Ordinal))
            return null;
        using var session = store.OpenAsyncSession();
        var snapshot = await session.LoadAsync<VenueLayoutSnapshot>(snapshotId, cancellationToken);
        if (snapshot is null)
            return null;
        var map = new SnapshotInventory(snapshot.Id!, snapshot.Plan, options.CurrentValue.ExcludedFromChains);
        var loaded = new Loaded(snapshot, map, SeatMapPacking.Payload(snapshot.Id!, snapshot.VenueName, snapshot.Plan), map.UnitsInMapOrder().ToArray());
        payloads.Set(snapshotId, loaded, new MemoryCacheEntryOptions { Size = 1, SlidingExpiration = TimeSpan.FromMinutes(30) });
        return loaded;
    }

    /// <summary>A short fingerprint of a frame, so an unchanged frame is not republished.</summary>
    internal static string Fingerprint(SeatStateFrame frame)
        => Convert.ToBase64String(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(frame.States + "|" + string.Join(',', frame.Blocks.Select(b => b.Available)))).AsSpan(0, 12));

    public void Dispose() => payloads.Dispose();
}

/// <summary>Anyone may follow a snapshot's seat states, anonymous buyers included: the frame says free, held or sold, never who.</summary>
internal sealed class VenueSeatTopicPolicy : ISparkLiveTopicPolicy
{
    public string Prefix => VenueSeatMaps.TopicPrefix;

    public bool CanSubscribe(string topic, SparkLiveViewer viewer) => true;

    public bool CanSubscribePrivate(string topic, SparkLiveViewer viewer) => false;
}
