using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authentication;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Abstractions.DateTimes;
using MintPlayer.Spark.Commerce.Inventory;
using MintPlayer.Spark.Commerce.Inventory.Live;
using MintPlayer.Spark.Commerce.Ticketing.Events.Contracts;
using MintPlayer.Spark.Commerce.Ticketing.Events.Discovery;
using MintPlayer.Spark.Commerce.Ticketing.Events.Models;
using MintPlayer.Spark.Commerce.Ticketing.Events.Sales;
using MintPlayer.Spark.Commerce.Ticketing.Venues.Models;
using MintPlayer.Spark.Commerce.Ticketing.Venues.Services;
using MintPlayer.Spark.Organizations;
using MintPlayer.Spark.Organizations.Models;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Commerce.Ticketing.Events.Services;

/// <summary>
/// Ticketing events (<c>docs/guide-ticketing-events.md</c>): the public event page's data, availability per date, the
/// one-screen event and the <c>.ics</c> of a date. Events, dates, ticket types and phases are edited as persistent
/// objects (the organization's members, X8); this service serves the storefront.
/// </summary>
public interface ISparkTicketEvents
{
    /// <summary>A published event by its slug, as the public page shows it; null when there is none.</summary>
    Task<SparkPublicEvent?> GetPublicAsync(string slug, CancellationToken cancellationToken = default);

    /// <summary>Places left per date of a published event (of one phase's dates when <paramref name="phaseId"/> is given).</summary>
    Task<SparkEventAvailability?> GetAvailabilityAsync(string eventId, string? phaseId = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// A one-off event on one screen (§4.11): the event, one date, one phase and the ticket types, with defaults, by a
    /// member of the organization; published at once when asked (and allowed, X8).
    /// </summary>
    Task<SparkQuickEventResult> QuickCreateAsync(SparkQuickEventRequest request, CancellationToken cancellationToken = default);

    /// <summary>The iCalendar file of a published event's date (GB9), or null.</summary>
    Task<string?> GetCalendarAsync(string dateId, string? culture = null, CancellationToken cancellationToken = default);
}

/// <summary>The price of a phase × ticket type × category as a sellable item, for the price history (GA3).</summary>
internal sealed record TicketPriceItem(string Id, string Channel, string? OrganizationId, Money? Price, int? VatRateBasisPoints) : ISellableItem
{
    string? ISellableItem.Id => Id;

    public PriceBasis PriceBasis => PriceBasis.Gross;

    public static TicketPriceItem For(TicketEvent ev, TicketSalePhase phase, TicketPhasePrice price, TicketType? type)
        => new(TicketItemIds.PriceItemId(phase.Id!, price.TicketTypeId, price.Category), ev.Channel, ev.OrganizationId, price.Price, type?.VatRateBasisPoints);
}

internal sealed class SparkTicketEvents(
    IDocumentStore store,
    IServiceProvider services,
    ISparkInventory inventory,
    ISparkVenues venues,
    IFinalPriceCalculator prices,
    ISparkPriceHistory priceHistory,
    TicketEventPublisher publisher,
    IOptionsMonitor<SparkCommerceOptions> commerce,
    IOptionsMonitor<SparkTicketingEventsOptions> options,
    TimeProvider clock) : ISparkTicketEvents
{
    // ---- public page -----------------------------------------------------------------------------------------------

    public async Task<SparkPublicEvent?> GetPublicAsync(string slug, CancellationToken cancellationToken = default)
    {
        using var session = store.OpenAsyncSession();
        if (await PublishedAsync(session, slug, cancellationToken) is not { } graph)
            return null;
        var ev = graph.Event;
        var now = clock.GetUtcNow();
        var organizer = ev.OrganizationId is null ? null : await session.LoadAsync<Organization>(ev.OrganizationId, cancellationToken);
        var allDates = graph.Dates.Select(d => d.Id!).ToList();
        var openPhases = graph.Phases.Where(p => SaleWindows.IsOpen(p, now)).ToList();

        var dates = new List<SparkPublicDate>();
        foreach (var date in graph.Dates)
        {
            var place = await PlaceAsync(session, date, cancellationToken);
            var onSale = SaleWindows.IsSellable(date, now) && openPhases.Any(p => SaleWindows.PhaseDates(p, allDates).Contains(date.Id!));
            dates.Add(new SparkPublicDate(date.Id!, date.Name, date.StartsAt, date.DoorsOpenAt, date.EndsAt, date.VenueId,
                place?.Name, place?.Lines ?? [], date.VenueTimeZoneId, date.LayoutSnapshotId, date.Status.ToString(), onSale));
        }

        var types = graph.Types.Select(t => new SparkPublicTicketType(t.Id!, t.Name, t.Description, t.Kind.ToString(), t.DateIds, t.ParentTypeIds,
            t.MinimumAge, t.AllowedSeatTags, t.Questions.Count > 0)).ToList();
        var typeById = graph.Types.ToDictionary(t => t.Id!, StringComparer.Ordinal);
        var phases = graph.Phases.Select(p => new SparkPublicPhase(
            p.Id!, p.Name, p.StartsAt, p.EndsAt, SaleWindows.PhaseDates(p, allDates).ToList(), SaleWindows.IsOpen(p, now),
            !string.IsNullOrEmpty(p.PresaleCodeHash), p.QueueEnabled, p.QueueOpensAt,
            p.QueueEnabled ? TicketScopes.Admission(p.Id!) : null, p.PreSelect,
            p.Prices.Where(price => typeById.ContainsKey(price.TicketTypeId))
                .Select(price => new SparkPublicPrice(price.TicketTypeId, price.Category ?? "", price.Price, FinalUnit(ev, price, typeById[price.TicketTypeId])))
                .ToList())).ToList();

        return new SparkPublicEvent(ev.Id!, ev.Slug, ev.Name, ev.Description, ev.ImageUrl, ev.Status.ToString(), ev.MinimumAge, ev.Performers,
            ev.OrganizationId, organizer?.Name, ev.Channel, graph.Phases.SelectMany(p => p.Prices).Select(p => p.Price.Currency).FirstOrDefault(),
            dates, types, phases);
    }

    /// <summary>The FinalPrice of one ticket (§4.4): the unit price with every mandatory fee of the channel.</summary>
    internal Money FinalUnit(TicketEvent ev, TicketPhasePrice price, TicketType type)
        => commerce.CurrentValue.Channels.ContainsKey(ev.Channel)
            ? prices.Calculate(new FinalPriceRequest(ev.Channel, price.Price, PriceBasis.Gross, type.VatRateBasisPoints)).UnitTotal
            : price.Price;

    internal static async Task<TicketEventGraph?> PublishedAsync(IAsyncDocumentSession session, string slug, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(slug))
            return null;
        var ev = await session.Query<TicketEvent>()
            .Where(e => e.Slug == slug && e.IsPublished == true)
            .FirstOrDefaultAsync(cancellationToken);
        return ev is null ? null : await TicketEventGraph.LoadAsync(session, ev, cancellationToken);
    }

    /// <summary>A venue's name and address lines: from the date's frozen snapshot, else the venue itself.</summary>
    internal async Task<(string Name, IReadOnlyList<string> Lines, Venue? Venue)?> PlaceAsync(IAsyncDocumentSession session, TicketEventDate date, CancellationToken cancellationToken)
    {
        Venue? venue = date.VenueId is { Length: > 0 } venueId ? await session.LoadAsync<Venue>(venueId, cancellationToken) : null;
        if (date.LayoutSnapshotId is { Length: > 0 } snapshotId && await venues.GetSnapshotAsync(snapshotId, cancellationToken) is { } snapshot)
            return (snapshot.VenueName, snapshot.AddressLines, venue);
        return venue is null ? null : (venue.Name, venue.Address?.Lines ?? [], venue);
    }

    // ---- availability ----------------------------------------------------------------------------------------------

    public async Task<SparkEventAvailability?> GetAvailabilityAsync(string eventId, string? phaseId = null, CancellationToken cancellationToken = default)
    {
        using var session = store.OpenAsyncSession();
        var ev = await session.LoadAsync<TicketEvent>(eventId, cancellationToken);
        if (ev is null || !ev.IsPublished)
            return null;
        var graph = await TicketEventGraph.LoadAsync(session, ev, cancellationToken);
        var allDates = graph.Dates.Select(d => d.Id!).ToList();
        var phase = phaseId is null ? null : graph.Phases.FirstOrDefault(p => p.Id == phaseId);
        if (phaseId is not null && phase is null)
            return null;
        var dateIds = phase is null ? allDates : SaleWindows.PhaseDates(phase, allDates).ToList();

        var result = new List<SparkDateAvailability>();
        foreach (var date in graph.Dates.Where(d => dateIds.Contains(d.Id!)))
        {
            var topics = new List<string>();
            int? available = null;
            var soldOut = false;
            var holds = false;
            if (string.IsNullOrEmpty(date.LayoutSnapshotId))
            {
                var quotaId = TicketQuotas.Date(date.Id!);
                if (await TryAvailabilityAsync(quotaId, cancellationToken) is { } dateQuota)
                {
                    available = dateQuota.Available;
                    soldOut = dateQuota.Available == 0;
                    holds = dateQuota.Held > 0;
                    topics.Add(InventoryAvailabilityPublisher.TopicPrefix + quotaId);
                }
            }
            if (phase?.CapacityPerDate is not null && await TryAvailabilityAsync(TicketQuotas.PhaseOnDate(date.Id!, phase.Id!), cancellationToken) is { } phaseQuota)
            {
                available = available is null ? phaseQuota.Available : Math.Min(available.Value, phaseQuota.Available);
                soldOut |= phaseQuota.Available == 0;
                holds |= phaseQuota.Held > 0;
                topics.Add(InventoryAvailabilityPublisher.TopicPrefix + phaseQuota.QuotaId);
            }
            var typeAvailability = new List<SparkTypeAvailability>();
            foreach (var type in graph.Types.Where(t => t.CapacityPerDate is not null && TicketEventPublisher.AppliesTo(t, date)))
            {
                if (await TryAvailabilityAsync(TicketQuotas.TypeOnDate(date.Id!, type.Id!), cancellationToken) is { } typeQuota)
                {
                    typeAvailability.Add(new SparkTypeAvailability(type.Id!, typeQuota.Available));
                    topics.Add(InventoryAvailabilityPublisher.TopicPrefix + typeQuota.QuotaId);
                }
            }
            result.Add(new SparkDateAvailability(date.Id!, available, soldOut, soldOut && holds, topics, typeAvailability));
        }
        return new SparkEventAvailability(ev.Id!, phaseId, result);
    }

    private async Task<QuotaAvailability?> TryAvailabilityAsync(string quotaId, CancellationToken cancellationToken)
    {
        try
        {
            return await inventory.GetAvailabilityAsync(quotaId, cancellationToken);
        }
        catch (InvalidOperationException)
        {
            return null; // not provisioned (yet)
        }
    }

    // ---- one-screen event ------------------------------------------------------------------------------------------

    public async Task<SparkQuickEventResult> QuickCreateAsync(SparkQuickEventRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var userId = services.GetService<ISparkCurrentUser>() is { IsAuthenticated: true, Id: { Length: > 0 } id } ? id
            : throw new SparkAccessDeniedException("New/TicketEvent");
        var organizations = services.GetRequiredService<ISparkOrganizations>();
        var role = await organizations.GetRoleAsync(request.OrganizationId, userId, cancellationToken);
        if (role is null || !RequiresOrganizationRoleAttribute.For(typeof(TicketEvent), "New").IsMetBy(role.Value, []))
            throw new SparkAccessDeniedException("New/TicketEvent");
        if (request.Publish && !RequiresOrganizationRoleAttribute.For(typeof(TicketEvent), "Publish").IsMetBy(role.Value, []))
            throw TicketingException.Of(TicketingRefusals.Forbidden, "Publish");

        var settings = commerce.CurrentValue;
        var channel = string.IsNullOrWhiteSpace(request.Channel) ? options.CurrentValue.DefaultChannel : request.Channel!;
        if (!settings.Channels.ContainsKey(channel))
            throw TicketingException.Of(TicketingRefusals.UnknownChannel, channel);
        if (request.Name is null || request.Name.IsEmpty || request.TicketTypes.Count == 0)
            throw TicketingException.Of(TicketingRefusals.InvalidEvent, "name");
        if (string.IsNullOrEmpty(request.LayoutId) && request.Capacity <= 0)
            throw TicketingException.Of(TicketingRefusals.InvalidEvent, "capacity");
        var currency = options.CurrentValue.DefaultCurrency ?? settings.DefaultCurrency;
        var now = clock.GetUtcNow();

        using var session = store.OpenAsyncSession();
        var ev = new TicketEvent
        {
            OrganizationId = request.OrganizationId,
            Name = request.Name,
            Description = request.Description,
            Channel = channel,
            Slug = await UniqueSlugAsync(session, TicketEventPublisher.Slugify(request.Name.GetValue(TranslatedString.DefaultLanguage)), cancellationToken),
            CreatedAt = now,
            UpdatedAt = now,
        };
        await session.StoreAsync(ev, cancellationToken);
        var date = new TicketEventDate
        {
            OrganizationId = request.OrganizationId,
            EventId = ev.Id!,
            StartsAt = request.StartsAt,
            DoorsOpenAt = request.DoorsOpenAt,
            EndsAt = request.EndsAt,
            VenueId = request.VenueId,
            LayoutId = request.LayoutId,
            Capacity = string.IsNullOrEmpty(request.LayoutId) ? request.Capacity : 0,
        };
        await session.StoreAsync(date, cancellationToken);
        var types = new List<TicketType>();
        var phase = new TicketSalePhase
        {
            OrganizationId = request.OrganizationId,
            EventId = ev.Id!,
            Name = TranslatedString.FromKey("ticketingEvents.phase.default"),
            StartsAt = request.SaleStartsAt ?? now,
            EndsAt = request.SaleEndsAt,
        };
        var order = 0;
        foreach (var quick in request.TicketTypes)
        {
            if (quick.Name is null || quick.Name.IsEmpty || quick.Price < 0)
                throw TicketingException.Of(TicketingRefusals.InvalidEvent, "ticketTypes");
            // One type per name: a seated date prices the same type per category.
            var type = types.FirstOrDefault(t => t.Name.Equals(quick.Name));
            if (type is null)
            {
                type = new TicketType { OrganizationId = request.OrganizationId, EventId = ev.Id!, Name = quick.Name, CapacityPerDate = quick.CapacityPerDate, SortOrder = order++ };
                await session.StoreAsync(type, cancellationToken);
                types.Add(type);
            }
            phase.Prices.Add(new TicketPhasePrice { TicketTypeId = type.Id!, Category = quick.Category ?? "", Price = new Money(quick.Price, currency) });
        }
        await session.StoreAsync(phase, cancellationToken);
        await session.SaveChangesAsync(cancellationToken);

        // GA3: the documents were written outside the persistent-object pipeline, so their first prices are recorded here.
        foreach (var price in phase.Prices)
            await priceHistory.RecordAsync(session, TicketPriceItem.For(ev, phase, price, types.First(t => t.Id == price.TicketTypeId)), null, "create", cancellationToken: cancellationToken);

        if (request.Publish)
        {
            await publisher.PublishAsync(session, ev, cancellationToken);
            ev.IsPublished = true;
        }
        await session.SaveChangesAsync(cancellationToken);
        return new SparkQuickEventResult(ev.Id!, ev.Slug, date.Id!, phase.Id!, types.Select(t => t.Id!).ToList(), ev.IsPublished);
    }

    internal static async Task<string> UniqueSlugAsync(IAsyncDocumentSession session, string slug, CancellationToken cancellationToken)
    {
        var taken = await session.Query<TicketEvent>()
            .Customize(c => c.WaitForNonStaleResults())
            .Where(e => e.Slug == slug || e.Slug.StartsWith(slug + "-"))
            .Select(e => e.Slug)
            .Take(1024)
            .ToListAsync(cancellationToken);
        if (!taken.Contains(slug))
            return slug;
        for (var n = 2; ; n++)
        {
            var candidate = $"{slug}-{n}";
            if (!taken.Contains(candidate))
                return candidate;
        }
    }

    // ---- .ics (GB9) ------------------------------------------------------------------------------------------------

    public async Task<string?> GetCalendarAsync(string dateId, string? culture = null, CancellationToken cancellationToken = default)
    {
        using var session = store.OpenAsyncSession();
        var date = await session.LoadAsync<TicketEventDate>(dateId, cancellationToken);
        var ev = date is null ? null : await session.LoadAsync<TicketEvent>(date.EventId, cancellationToken);
        if (date is null || ev is null || !ev.IsPublished)
            return null;
        var language = string.IsNullOrWhiteSpace(culture) ? TranslatedString.DefaultLanguage : culture!;
        var place = await PlaceAsync(session, date, cancellationToken);
        var pageUrl = PageUrl(ev.Slug);
        var description = new List<string>();
        if (date.DoorsOpenAt is { } doors && services.GetService<ISparkDateTimeFormatter>() is { } formatter)
        {
            // K10: the doors-open time is formatted by the core facility, in the venue's zone (the calendar's own zone).
            var zone = date.VenueTimeZoneId ?? "UTC";
            var text = await formatter.FormatAsync(doors, new SparkRecipientLocale(zone, language),
                new SparkDateTimeFormatOptions { Style = ESparkDateTimeStyle.Time }, cancellationToken);
            description.Add(Translate(language, "doors") + ": " + text);
        }
        if (pageUrl is not null)
            description.Add(pageUrl);
        var entry = new EventCalendarEntry(
            Uid: $"{date.Id}@ticketing",
            Summary: string.Join(" · ", new[] { ev.Name.GetValue(language), date.Name?.GetValue(language) }.Where(s => !string.IsNullOrWhiteSpace(s))),
            Start: date.StartsAt,
            End: date.EndsAt,
            Stamp: clock.GetUtcNow(),
            Description: string.Join("\n", description),
            Location: place is { } p ? string.Join(", ", new[] { p.Name }.Concat(p.Lines).Where(s => !string.IsNullOrWhiteSpace(s))) : null,
            Url: pageUrl,
            VenueTimeZoneId: date.VenueTimeZoneId,
            Cancelled: date.Status == ETicketEventStatus.Cancelled || ev.Status == ETicketEventStatus.Cancelled);
        return EventCalendar.Build([entry]);
    }

    /// <summary>The public page's path for <paramref name="slug"/>.</summary>
    internal string? PageUrl(string slug) => options.CurrentValue.EventPagePath.Replace("{slug}", Uri.EscapeDataString(slug), StringComparison.Ordinal);

    private static string Translate(string language, string key) => (language.Split('-')[0], key) switch
    {
        ("nl", "doors") => "Deuren open",
        ("fr", "doors") => "Ouverture des portes",
        ("de", "doors") => "Einlass",
        _ => "Doors open",
    };
}
