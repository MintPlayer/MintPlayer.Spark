using MintPlayer.Spark.Commerce.Ticketing.Events.Models;
using MintPlayer.Spark.Commerce.Ticketing.Events.Sales;
using MintPlayer.Spark.Commerce.Ticketing.Tickets.Models;
using MintPlayer.Spark.Commerce.Ticketing.Venues.Models;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Commerce.Ticketing.Tickets.Services;

/// <summary>What a ticket of a ticketing item is: its type, event, dates and the event's ticket settings.</summary>
internal sealed record TicketItemContext(TicketItemRef Item, TicketType Type, TicketEvent Event, IReadOnlyList<TicketEventDate> Dates, TicketEventSettings? Settings)
{
    /// <summary>The dates a ticket of this item admits to: the pass's own dates, or the item's date.</summary>
    public IReadOnlyList<string> DateIds => Item.IsPass ? Type.DateIds : [Item.DateId];
}

/// <summary>Loads the Events documents a ticket needs. Read-only; every method takes the caller's session.</summary>
internal static class TicketCatalog
{
    public static async Task<TicketItemContext?> LoadAsync(IAsyncDocumentSession session, string itemId, CancellationToken cancellationToken)
    {
        if (TicketItemIds.Parse(itemId) is not { } item)
            return null;
        var type = await session.LoadAsync<TicketType>(item.TicketTypeId, cancellationToken);
        if (type is null)
            return null;
        var ev = await session.LoadAsync<TicketEvent>(type.EventId, cancellationToken);
        if (ev is null)
            return null;
        var dateIds = item.IsPass ? type.DateIds : [item.DateId];
        var dates = (await session.LoadAsync<TicketEventDate>(dateIds.Distinct(StringComparer.Ordinal), cancellationToken))
            .Values.Where(d => d is not null).Select(d => d!).OrderBy(d => d.StartsAt).ToList();
        var settings = await SettingsAsync(session, ev.Id!, cancellationToken);
        return new TicketItemContext(item, type, ev, dates, settings);
    }

    public static async Task<TicketEventSettings?> SettingsAsync(IAsyncDocumentSession session, string eventId, CancellationToken cancellationToken)
        => await session.Query<TicketEventSettings>().Where(s => s.EventId == eventId).FirstOrDefaultAsync(cancellationToken);

    public static async Task<IReadOnlyList<TicketEventDate>> DatesAsync(IAsyncDocumentSession session, IEnumerable<string> dateIds, CancellationToken cancellationToken)
        => (await session.LoadAsync<TicketEventDate>(dateIds.Distinct(StringComparer.Ordinal), cancellationToken))
            .Values.Where(d => d is not null).Select(d => d!).OrderBy(d => d.StartsAt).ToList();

    /// <summary>The event's check-in lists (active ones), or the implicit default list.</summary>
    public static async Task<IReadOnlyList<TicketCheckInList>> ListsAsync(IAsyncDocumentSession session, TicketEvent ev, CancellationToken cancellationToken)
    {
        var eventId = ev.Id!;
        var lists = await session.Query<TicketCheckInList>().Where(l => l.EventId == eventId && l.IsActive).ToListAsync(cancellationToken);
        return lists.Count > 0 ? lists.OrderBy(l => l.SortOrder).ToList() : [TicketCheckInLists.Default(eventId, ev.OrganizationId)];
    }

    /// <summary>One list by id: a stored list, or the event's implicit default.</summary>
    public static async Task<TicketCheckInList?> ListAsync(IAsyncDocumentSession session, string listId, CancellationToken cancellationToken)
    {
        if (listId.StartsWith(TicketCheckInLists.DefaultPrefix, StringComparison.Ordinal))
        {
            var eventId = listId[TicketCheckInLists.DefaultPrefix.Length..];
            var ev = await session.LoadAsync<TicketEvent>(eventId, cancellationToken);
            if (ev is null)
                return null;
            // The implicit list exists only while the event has no list of its own.
            return await session.Query<TicketCheckInList>().AnyAsync(l => l.EventId == eventId && l.IsActive, cancellationToken)
                ? null
                : TicketCheckInLists.Default(eventId, ev.OrganizationId);
        }
        var list = await session.LoadAsync<TicketCheckInList>(listId, cancellationToken);
        return list is { IsActive: true } ? list : null;
    }
}

/// <summary>The times a ticket derives from its dates and the event's settings (D11, D30, retention). Pure.</summary>
internal static class TicketTimes
{
    /// <summary>D11: the first date's start minus the lead time; null (at once) without one, or when that moment has passed.</summary>
    public static DateTimeOffset? AvailableFrom(TicketEventSettings? settings, IReadOnlyList<TicketEventDate> dates, SparkTicketingTicketsOptions options, DateTimeOffset now)
    {
        if (settings?.DeliveryLeadTimeHours is not { } hours || dates.Count == 0)
            return null;
        var lead = TimeSpan.FromHours(Math.Max(0, hours));
        if (lead > options.Delivery.MaxLeadTime)
            lead = options.Delivery.MaxLeadTime;
        var at = dates.Min(d => d.StartsAt) - lead;
        return at > now ? at : null;
    }

    /// <summary>§4.11: the first doors (or start) minus the freeze; null without dates.</summary>
    public static DateTimeOffset? FreezeAt(TicketEventSettings? settings, IReadOnlyList<TicketEventDate> dates, SparkTicketingTicketsOptions options)
    {
        if (dates.Count == 0)
            return null;
        var freeze = settings?.FreezeBeforeDoorsMinutes is { } minutes ? TimeSpan.FromMinutes(Math.Max(0, minutes)) : options.Exchange.DefaultFreezeBeforeDoors;
        return dates.Min(d => d.DoorsOpenAt ?? d.StartsAt) - freeze;
    }

    /// <summary>The last date's end (or start) plus the holder retention.</summary>
    public static DateTimeOffset? HolderEraseAfter(IReadOnlyList<TicketEventDate> dates, SparkTicketingTicketsOptions options)
        => dates.Count == 0 ? null : dates.Max(d => d.EndsAt ?? d.StartsAt) + options.HolderRetention;

    /// <summary>The last date's end (or start + 12 h): device tokens and offline lists live until then plus the grace.</summary>
    public static DateTimeOffset EventEnd(IReadOnlyList<TicketEventDate> dates, DateTimeOffset fallback)
        => dates.Count == 0 ? fallback : dates.Max(d => d.EndsAt ?? d.StartsAt + CheckIn.CheckInRules.AfterStartWithoutEnd);
}

/// <summary>The printed label of a numbered seat (block · row · seat) from a layout snapshot. Pure.</summary>
internal static class SeatLabels
{
    public static string? For(VenueLayoutSnapshot? snapshot, string? unit)
    {
        if (snapshot?.Id is not { } snapshotId || unit is null || !unit.StartsWith(snapshotId + "/", StringComparison.Ordinal))
            return null;
        var seatKey = unit[(snapshotId.Length + 1)..];
        foreach (var block in snapshot.Plan.Blocks)
            foreach (var row in block.Rows)
                foreach (var seat in row.Seats)
                    if (seat.Key == seatKey)
                        return string.Join(" · ", new[] { block.Name, row.Label, seat.Label }.Where(s => !string.IsNullOrWhiteSpace(s)));
        return null;
    }
}
