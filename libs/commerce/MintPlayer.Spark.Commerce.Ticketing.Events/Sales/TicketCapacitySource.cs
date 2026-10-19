using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Commerce.Checkout;
using MintPlayer.Spark.Commerce.Ticketing.Events.Models;
using MintPlayer.Spark.Commerce.Ticketing.Venues;
using MintPlayer.Spark.Commerce.Ticketing.Venues.Plans;
using MintPlayer.Spark.Commerce.Ticketing.Venues.Services;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Commerce.Ticketing.Events.Sales;

/// <summary>
/// Ticket types as checkout sells them (PRD §4.1 "capacity sources"): handles every <c>ticketing:</c> item, loads the
/// event, phase, type and dates, and lets <see cref="TicketItemResolver"/> decide. Registered by
/// <c>AddTicketingEvents()</c> (scoped, <c>TryAddEnumerable</c>).
/// </summary>
internal sealed class TicketCapacitySource(
    IDocumentStore store,
    ITicketSeating seating,
    IOptionsMonitor<SparkCommerceOptions> commerce,
    TimeProvider clock) : ICheckoutCapacitySource
{
    public bool Handles(string itemId) => TicketItemIds.Handles(itemId);

    public async Task<CheckoutItem?> ResolveAsync(CheckoutItemRequest request, CancellationToken cancellationToken)
    {
        var context = await LoadAsync(store, seating, commerce.CurrentValue, request.ItemId, request.ParentItemId, cancellationToken);
        return context is null ? null : TicketItemResolver.Resolve(context, request, clock.GetUtcNow());
    }

    /// <summary>The documents behind <paramref name="itemId"/>, or null when any is missing (or the channel is unknown).</summary>
    internal static async Task<TicketItemContext?> LoadAsync(IDocumentStore store, ITicketSeating seating, SparkCommerceOptions commerce,
        string itemId, string? parentItemId, CancellationToken cancellationToken)
    {
        if (TicketItemIds.Parse(itemId) is not { } item)
            return null;
        var parentRef = TicketItemIds.Parse(parentItemId);

        using var session = store.OpenAsyncSession();
        var phase = await session.LoadAsync<TicketSalePhase>(item.PhaseId, cancellationToken);
        var type = await session.LoadAsync<TicketType>(item.TicketTypeId, cancellationToken);
        if (phase is null || type is null)
            return null;
        var ev = await session.LoadAsync<TicketEvent>(phase.EventId, cancellationToken);
        if (ev is null || !commerce.Channels.TryGetValue(ev.Channel, out var channel))
            return null;

        var dateIds = item.IsPass ? type.DateIds.Distinct(StringComparer.Ordinal).ToList() : [item.DateId];
        var loaded = await session.LoadAsync<TicketEventDate>(dateIds, cancellationToken);
        if (loaded.Values.Any(d => d is null))
            return null;
        var dates = dateIds.Select(id => loaded[id]!).ToList();
        var eventDateIds = await session.Query<TicketEventDate>()
            .Where(d => d.EventId == ev.Id)
            .Select(d => d.Id!)
            .Take(1024)
            .ToListAsync(cancellationToken);

        SnapshotInventory? plan = null;
        if (!item.IsPass && dates[0].LayoutSnapshotId is { Length: > 0 } snapshotId)
        {
            plan = await seating.GetAsync(snapshotId, cancellationToken);
            if (plan is null)
                return null;
        }

        TicketType? parentType = parentRef is null ? null : await session.LoadAsync<TicketType>(parentRef.TicketTypeId, cancellationToken);
        return new TicketItemContext(item, ev, phase, type, dates, eventDateIds, plan, channel, parentRef, parentType);
    }
}

/// <summary>The seating of a seated date: its frozen layout snapshot, as the inventory engine sees it (Ticketing.Venues).</summary>
public interface ITicketSeating
{
    /// <summary>The snapshot's seats, chains and blocks, or null when it does not exist.</summary>
    Task<SnapshotInventory?> GetAsync(string snapshotId, CancellationToken cancellationToken);
}

/// <summary>Reads snapshots through <see cref="ISparkVenues"/>. Snapshots never change (§4.11), so they are cached.</summary>
internal sealed class VenueTicketSeating(ISparkVenues venues, IMemoryCache cache, IOptionsMonitor<SparkTicketingVenuesOptions> venueOptions) : ITicketSeating
{
    public async Task<SnapshotInventory?> GetAsync(string snapshotId, CancellationToken cancellationToken)
    {
        var key = "ticketing-events:snapshot:" + snapshotId;
        if (cache.TryGetValue(key, out SnapshotInventory? cached))
            return cached;
        var snapshot = await venues.GetSnapshotAsync(snapshotId, cancellationToken);
        if (snapshot is null)
            return null;
        var inventory = new SnapshotInventory(snapshotId, snapshot.Plan, venueOptions.CurrentValue.ExcludedFromChains);
        cache.Set(key, inventory, new MemoryCacheEntryOptions { SlidingExpiration = TimeSpan.FromHours(1), Size = 1 });
        return inventory;
    }
}
