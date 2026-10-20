using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Commerce.Ticketing.Events.Models;
using MintPlayer.Spark.Commerce.Ticketing.Events.Sales;
using MintPlayer.Spark.Commerce.Ticketing.Events.Services;
using MintPlayer.Spark.Organizations.Models;
using MintPlayer.Spark.PageMeta;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Sitemap;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Commerce.Ticketing.Events.Discovery;

/// <summary>
/// The public event page's server-rendered tags (X6) and schema.org <c>Event</c> data (X22, GB9): one <c>Event</c> per date,
/// its start written in the venue's offset (§4.14) through the core zone facility (K10), its offers at
/// <c>FinalPrice</c> (§4.4). Answers null for anything that is not a published event (the SPA shows its own not-found).
/// </summary>
internal sealed class EventPageMetaProvider(
    IDocumentStore store,
    SparkTicketEvents events,
    IOptionsMonitor<SparkTicketingEventsOptions> options,
    TimeProvider clock) : ISparkPageMetaProvider
{
    public IEnumerable<string> RoutePatterns => [options.CurrentValue.EventPagePath];

    public async Task<SparkPageMeta?> GetAsync(SparkPageMetaContext context, CancellationToken cancellationToken)
    {
        if (!options.CurrentValue.PublicPages || context.RouteValues["slug"] is not string slug)
            return null;
        using var session = store.OpenAsyncSession();
        if (await SparkTicketEvents.PublishedAsync(session, slug, cancellationToken) is not { } graph)
            return null;
        var ev = graph.Event;
        var languages = context.HttpContext.Request.GetTypedHeaders().AcceptLanguage
            .OrderByDescending(l => l.Quality ?? 1).Select(l => l.Value.Value).ToList();
        var name = ev.Name.Resolve(languages);
        var description = ev.Description?.Resolve(languages) ?? name;
        var organizer = ev.OrganizationId is null ? null : await session.LoadAsync<Organization>(ev.OrganizationId, cancellationToken);
        var pageUrl = events.PageUrl(ev.Slug);
        var now = clock.GetUtcNow();
        var allDates = graph.Dates.Select(d => d.Id!).ToList();
        var typeById = graph.Types.ToDictionary(t => t.Id!, StringComparer.Ordinal);

        var data = new List<SparkStructuredData>();
        foreach (var date in graph.Dates)
        {
            var zone = SparkTimeZones.FindOrUtc(date.VenueTimeZoneId);
            var place = await events.PlaceAsync(session, date, cancellationToken);
            var offers = graph.Phases
                .Where(p => SaleWindows.PhaseDates(p, allDates).Contains(date.Id!) && (p.EndsAt is not { } end || end > now))
                .SelectMany(p => p.Prices.Where(price => typeById.TryGetValue(price.TicketTypeId, out var t) && t.Kind != ETicketTypeKind.AddOn)
                    .Select(price => new SparkOffer(
                        events.FinalUnit(ev, price, typeById[price.TicketTypeId]),
                        SaleWindows.IsOpen(p, now) ? SparkAvailability.InStock : SparkAvailability.PreSale,
                        pageUrl,
                        typeById[price.TicketTypeId].Name.Resolve(languages),
                        SparkTimeZones.ToZone(p.StartsAt, zone),
                        p.EndsAt is { } until ? SparkTimeZones.ToZone(until, zone) : null)))
                .ToList();
            data.Add(new SparkEventData(
                Name: date.Name is { IsEmpty: false } dateName ? $"{name} · {dateName.Resolve(languages)}" : name,
                Start: SparkTimeZones.ToZone(date.StartsAt, zone),
                Location: place is { } p2 ? Place(p2.Name, p2.Venue) : null,
                Description: description,
                End: date.EndsAt is { } endsAt ? SparkTimeZones.ToZone(endsAt, zone) : null,
                Status: Status(date.Status == ETicketEventStatus.Scheduled ? ev.Status : date.Status),
                Images: ev.ImageUrl is { Length: > 0 } image ? [image] : null,
                Offers: offers,
                Organizer: organizer is null ? null : new SparkOrganization(organizer.Name),
                Performers: ev.Performers.Count > 0 ? ev.Performers : null,
                PreviousStart: date.PreviousStartsAt is { } previous ? SparkTimeZones.ToZone(previous, zone) : null));
        }

        Uri? imageUri = ev.ImageUrl is { Length: > 0 } url && Uri.TryCreate(url, UriKind.RelativeOrAbsolute, out var parsed) ? parsed : null;
        return new SparkPageMeta(name, Truncate(description, 300), imageUri, "website", StructuredData: data);
    }

    private static SparkPlace Place(string name, Venues.Models.Venue? venue)
    {
        var address = venue?.Address;
        var street = address is null ? null : string.Join(" ", new[] { address.StreetName, address.HouseNumber }.Where(s => !string.IsNullOrWhiteSpace(s)));
        return new SparkPlace(name, string.IsNullOrWhiteSpace(street) ? null : street, address?.LocalityName, address?.PostalCode, null, address?.CountryCode);
    }

    private static SparkEventStatus Status(ETicketEventStatus status) => status switch
    {
        ETicketEventStatus.Cancelled => SparkEventStatus.Cancelled,
        ETicketEventStatus.Postponed => SparkEventStatus.Postponed,
        ETicketEventStatus.Rescheduled => SparkEventStatus.Rescheduled,
        _ => SparkEventStatus.Scheduled,
    };

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..(max - 1)].TrimEnd() + "…";
}

/// <summary>Published events in <c>/sitemap.xml</c> (X22, GB9), as <c>/sitemaps/events/{page}.xml</c>.</summary>
internal sealed class EventSitemapSource(IDocumentStore store, IOptionsMonitor<SparkTicketingEventsOptions> options) : ISparkSitemapSource
{
    public string Name => "events";

    public async Task<int> CountAsync(SparkSitemapContext context, CancellationToken cancellationToken)
    {
        if (!options.CurrentValue.PublicPages)
            return 0;
        using var session = store.OpenAsyncSession();
        return await session.Query<TicketEvent>().Where(e => e.IsPublished == true).CountAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<SparkSitemapEntry>> GetEntriesAsync(SparkSitemapContext context, int skip, int take, CancellationToken cancellationToken)
    {
        if (!options.CurrentValue.PublicPages)
            return [];
        using var session = store.OpenAsyncSession();
        var page = await session.Query<TicketEvent>()
            .Where(e => e.IsPublished == true)
            .OrderBy(e => e.Id)
            .Skip(skip)
            .Take(take)
            .Select(e => new { e.Slug, e.UpdatedAt })
            .ToListAsync(cancellationToken);
        var path = options.CurrentValue.EventPagePath;
        return page.Select(e => new SparkSitemapEntry(path.Replace("{slug}", Uri.EscapeDataString(e.Slug), StringComparison.Ordinal), e.UpdatedAt)).ToList();
    }
}

/// <summary>DSA notices (GA5, Art. 16) on events: kind <c>event</c>, the seller is the responsible party.</summary>
internal sealed class EventDsaTargetResolver(IDocumentStore store) : IDsaNoticeTargetResolver
{
    public IReadOnlyCollection<string> Kinds { get; } = ["event"];

    public async Task<DsaNoticeTarget?> ResolveAsync(string kind, string targetId, CancellationToken cancellationToken = default)
    {
        using var session = store.OpenAsyncSession();
        var ev = await session.LoadAsync<TicketEvent>(targetId, cancellationToken);
        return ev is { IsPublished: true } ? new DsaNoticeTarget(ev.Id!, ev.OrganizationId, ev.Name.GetValue(TranslatedString.DefaultLanguage)) : null;
    }
}

/// <summary>
/// The ranking of the public event list (GA5: CRD Art. 6a(1)(a), P2B Art. 5): by the next date's start, then by name.
/// No paid placement.
/// </summary>
internal sealed class EventRankingParameters : IRankingParameterSource
{
    public IEnumerable<RankingParameter> GetParameters() =>
    [
        new("ticketing.events", "ticketingEvents.ranking.nextDate", "ticketingEvents.ranking.nextDateDescription", 1),
        new("ticketing.events", "ticketingEvents.ranking.name", "ticketingEvents.ranking.nameDescription", 2),
    ];
}
