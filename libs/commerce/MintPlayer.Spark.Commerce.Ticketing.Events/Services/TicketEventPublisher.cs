using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Commerce.Inventory;
using MintPlayer.Spark.Commerce.Ticketing.Events.Models;
using MintPlayer.Spark.Commerce.Ticketing.Events.Sales;
using MintPlayer.Spark.Commerce.Ticketing.Venues.Models;
using MintPlayer.Spark.Commerce.Ticketing.Venues.Services;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Commerce.Ticketing.Events.Services;

/// <summary>An event with its dates, ticket types and phases.</summary>
public sealed record TicketEventGraph(
    TicketEvent Event,
    IReadOnlyList<TicketEventDate> Dates,
    IReadOnlyList<TicketType> Types,
    IReadOnlyList<TicketSalePhase> Phases)
{
    /// <summary>Loads the children of <paramref name="ev"/> in <paramref name="session"/> (tracked: changes are saved with it).</summary>
    public static async Task<TicketEventGraph> LoadAsync(IAsyncDocumentSession session, TicketEvent ev, CancellationToken cancellationToken)
    {
        var id = ev.Id ?? "";
        var dates = await session.Query<TicketEventDate>().Customize(c => c.WaitForNonStaleResults()).Where(d => d.EventId == id).Take(1024).ToListAsync(cancellationToken);
        var types = await session.Query<TicketType>().Customize(c => c.WaitForNonStaleResults()).Where(t => t.EventId == id).Take(1024).ToListAsync(cancellationToken);
        var phases = await session.Query<TicketSalePhase>().Customize(c => c.WaitForNonStaleResults()).Where(p => p.EventId == id).Take(1024).ToListAsync(cancellationToken);
        return new TicketEventGraph(ev,
            dates.OrderBy(d => d.SortOrder).ThenBy(d => d.StartsAt).ToList(),
            types.OrderBy(t => t.SortOrder).ToList(),
            phases.OrderBy(p => p.SortOrder).ThenBy(p => p.StartsAt).ToList());
    }
}

/// <summary>
/// Publication (X8, §4.11): what must hold before an event sells, and the provisioning that makes it sell: every date's
/// quotas, and a seated date's frozen layout snapshot (§4.11 "a published date freezes a layout snapshot").
/// </summary>
internal sealed partial class TicketEventPublisher(
    ISparkInventory inventory,
    ISparkVenues venues,
    ISparkSellers sellers,
    ISparkLegalDocuments legal,
    IOptionsMonitor<SparkCommerceOptions> commerce,
    IOptionsMonitor<SparkTicketingEventsOptions> options,
    TimeProvider clock)
{
    [GeneratedRegex("^[a-z0-9]+(?:-[a-z0-9]+)*$")]
    private static partial Regex SlugPattern();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_.-]{0,63}$")]
    private static partial Regex KeyPattern();

    public static bool IsSlug(string? slug) => slug is { Length: > 0 and <= 120 } && SlugPattern().IsMatch(slug);

    public static bool IsKey(string? key) => key is not null && KeyPattern().IsMatch(key);

    /// <summary>A slug from a name: lower-case ASCII letters and digits, dashes between words.</summary>
    public static string Slugify(string? name)
    {
        var normalized = (name ?? "").Normalize(System.Text.NormalizationForm.FormD);
        var builder = new System.Text.StringBuilder();
        var dash = false;
        foreach (var c in normalized.ToLowerInvariant())
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.NonSpacingMark)
                continue;
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                builder.Append(c);
                dash = false;
            }
            else if (!dash && builder.Length > 0)
            {
                builder.Append('-');
                dash = true;
            }
        }
        var slug = builder.ToString().Trim('-');
        if (slug.Length > 100)
            slug = slug[..100].Trim('-');
        return slug.Length == 0 ? "event" : slug;
    }

    /// <summary>
    /// What prevents <paramref name="graph"/> from selling (English, for the validation message), or nothing. Pure: the
    /// seller gate and the legal documents are checked by <see cref="PublishAsync"/>.
    /// </summary>
    public static List<string> Problems(TicketEventGraph graph, SparkCommerceOptions commerce)
    {
        var (ev, dates, types, phases) = (graph.Event, graph.Dates, graph.Types, graph.Phases);
        var problems = new List<string>();
        if (ev.Name.IsEmpty)
            problems.Add("The event has no name.");
        if (!IsSlug(ev.Slug))
            problems.Add($"The slug '{ev.Slug}' is not lower-case letters, digits and dashes.");
        if (!commerce.Channels.TryGetValue(ev.Channel, out var channel))
        {
            problems.Add($"The channel '{ev.Channel}' is not configured in Spark:Commerce:Channels.");
            return problems;
        }

        var dateIds = dates.Select(d => d.Id!).ToHashSet(StringComparer.Ordinal);
        var typeById = types.ToDictionary(t => t.Id!, StringComparer.Ordinal);
        if (dates.Count == 0)
            problems.Add("The event has no date.");
        foreach (var date in dates)
        {
            if (date.EndsAt is { } end && end <= date.StartsAt)
                problems.Add($"Date {date.Id} ends before it starts.");
            if (date.DoorsOpenAt is { } doors && doors > date.StartsAt)
                problems.Add($"Date {date.Id} opens its doors after it starts.");
            if (string.IsNullOrEmpty(date.LayoutId) && date.Capacity <= 0)
                problems.Add($"Date {date.Id} has neither a layout nor a capacity.");
            if (date.VenueTimeZoneId is { Length: > 0 } zone && !MintPlayer.Spark.Services.SparkTimeZones.IsKnown(zone))
                problems.Add($"Date {date.Id} names the unknown time zone '{zone}'.");
        }

        if (!types.Any(t => t.Kind != ETicketTypeKind.AddOn))
            problems.Add("The event has no admission ticket type.");
        foreach (var type in types)
        {
            if (type.Name.IsEmpty)
                problems.Add($"Ticket type {type.Id} has no name.");
            if (type.DateIds.Any(d => !dateIds.Contains(d)))
                problems.Add($"Ticket type {type.Id} names a date of another event.");
            if (type.Kind == ETicketTypeKind.Pass)
            {
                if (type.DateIds.Distinct().Count() < 2)
                    problems.Add($"Pass {type.Id} must be valid on at least two dates.");
                if (dates.Where(d => type.DateIds.Contains(d.Id!)).Any(d => !string.IsNullOrEmpty(d.LayoutId)))
                    problems.Add($"Pass {type.Id} covers a seated date; passes are sold for general-admission dates only.");
            }
            if (type.Kind == ETicketTypeKind.AddOn
                && type.ParentTypeIds.Any(p => !typeById.TryGetValue(p, out var parent) || parent.Kind == ETicketTypeKind.AddOn))
                problems.Add($"Add-on {type.Id} names a parent that is not an admission or pass of this event.");
            if (type.CapacityPerDate is < 0)
                problems.Add($"Ticket type {type.Id} has a negative capacity.");
            problems.AddRange(Answers.AttendeeQuestionRules.Problems(type));
        }

        var currencies = phases.SelectMany(p => p.Prices).Select(p => p.Price.Currency).Distinct(StringComparer.Ordinal).ToList();
        if (currencies.Count > 1)
            problems.Add("The event's prices use more than one currency.");
        if (!phases.Any(p => p.Prices.Count > 0))
            problems.Add("No sale phase offers a priced ticket type.");
        var policy = SalePolicies.Effective(ev.Policy, channel);
        foreach (var phase in phases)
        {
            if (phase.EndsAt is { } end && end <= phase.StartsAt)
                problems.Add($"Phase {phase.Id} ends before it starts.");
            if (phase.DateIds.Any(d => !dateIds.Contains(d)))
                problems.Add($"Phase {phase.Id} names a date of another event.");
            if (phase.CapacityPerDate is < 0)
                problems.Add($"Phase {phase.Id} has a negative capacity.");
            if (phase.QueueEnabled && policy.QueueMode is null)
                problems.Add($"Phase {phase.Id} is queued, but channel '{ev.Channel}' allows no waiting-room mode.");
            if (phase.PreSelect && !phase.QueueEnabled)
                problems.Add($"Phase {phase.Id} pre-selects without a waiting room (D25: the pre-selection is held at admission).");
            foreach (var price in phase.Prices)
            {
                if (!typeById.ContainsKey(price.TicketTypeId))
                    problems.Add($"Phase {phase.Id} prices the ticket type {price.TicketTypeId}, which is not of this event.");
                if (price.Price.Amount < 0)
                    problems.Add($"Phase {phase.Id} has a negative price.");
            }
            foreach (var duplicate in phase.Prices.GroupBy(p => (p.TicketTypeId, p.Category ?? "")).Where(g => g.Count() > 1))
                problems.Add($"Phase {phase.Id} prices ticket type {duplicate.Key.TicketTypeId} twice in category '{duplicate.Key.Item2}'.");
        }
        return problems;
    }

    /// <summary>
    /// Refuses publication when <see cref="Problems"/>, the seller gate (D21, D43) or the channel's required legal
    /// documents (GA2) say so; otherwise provisions every date (quotas, snapshots) in <paramref name="session"/>.
    /// </summary>
    public async Task PublishAsync(IAsyncDocumentSession session, TicketEvent ev, CancellationToken cancellationToken)
    {
        var settings = commerce.CurrentValue;
        var graph = await TicketEventGraph.LoadAsync(session, ev, cancellationToken);
        var problems = Problems(graph, settings);
        if (problems.Count == 0)
        {
            var channel = settings.Channels[ev.Channel];
            var gate = await sellers.CanSellAsync(ev.OrganizationId, ev.Channel, cancellationToken);
            if (!gate.Allowed)
                problems.Add("The seller may not sell in this channel: " + string.Join(", ", gate.Reasons) + ".");
            var missing = await legal.GetMissingAsync(ev.OrganizationId, channel.RequiredLegalDocuments, cancellationToken);
            if (missing.Count > 0)
                problems.Add("Required legal documents are not published: " + string.Join(", ", missing) + " (GA2).");
        }
        if (problems.Count > 0)
            throw new SparkValidationException(string.Join(" ", problems), nameof(TicketEvent.IsPublished));

        var liveUntil = LiveUntil(graph);
        foreach (var date in graph.Dates)
            await ProvisionDateAsync(session, date, graph, liveUntil, cancellationToken);
        ev.PublishedAt ??= clock.GetUtcNow();
    }

    /// <summary>The end of the last sale window: until then seated dates publish their seat states live.</summary>
    internal static DateTimeOffset LiveUntil(TicketEventGraph graph)
    {
        var lastDate = graph.Dates.Count == 0 ? DateTimeOffset.MinValue : graph.Dates.Max(d => d.EndsAt ?? d.StartsAt);
        var lastPhase = graph.Phases.Select(p => p.EndsAt ?? lastDate).DefaultIfEmpty(lastDate).Max();
        return lastPhase > lastDate ? lastDate : lastPhase;
    }

    /// <summary>
    /// Makes one date sellable (idempotent): freezes its layout snapshot and provisions it (seated), or creates its
    /// general-admission quota; creates or resizes the per-type and per-phase quotas that apply to it.
    /// </summary>
    public async Task ProvisionDateAsync(IAsyncDocumentSession session, TicketEventDate date, TicketEventGraph graph, DateTimeOffset liveUntil, CancellationToken cancellationToken)
    {
        var shards = options.CurrentValue.ShardsPerQuota;
        if (!string.IsNullOrEmpty(date.LayoutId))
        {
            if (string.IsNullOrEmpty(date.LayoutSnapshotId))
                date.LayoutSnapshotId = await venues.SnapshotAsync(date.LayoutId, date.Id!, date.LayoutVersion, cancellationToken);
            await venues.ProvisionAsync(date.LayoutSnapshotId, cancellationToken);
            await venues.SetLiveAsync(date.LayoutSnapshotId, liveUntil.UtcDateTime, cancellationToken);
            if (await venues.GetSnapshotAsync(date.LayoutSnapshotId, cancellationToken) is { } snapshot)
            {
                date.VenueId ??= snapshot.VenueId;
                date.VenueTimeZoneId ??= snapshot.TimeZone;
                var categories = snapshot.Plan.Categories.Select(c => c.Key).ToHashSet(StringComparer.Ordinal);
                var unknown = graph.Phases.Where(p => SaleWindows.PhaseDates(p, graph.Dates.Select(d => d.Id!)).Contains(date.Id!))
                    .SelectMany(p => p.Prices).Select(p => p.Category ?? "").Where(c => c.Length > 0 && !categories.Contains(c)).Distinct().ToList();
                if (unknown.Count > 0)
                    throw new SparkValidationException($"Date {date.Id}: the layout has no price category {string.Join(", ", unknown)}.", nameof(TicketEvent.IsPublished));
            }
        }
        else
        {
            await EnsureQuotaAsync(TicketQuotas.Date(date.Id!), date.Capacity, shards, cancellationToken);
            if (date.VenueTimeZoneId is null && date.VenueId is { Length: > 0 } venueId
                && await session.LoadAsync<Venue>(venueId, cancellationToken) is { TimeZone: { Length: > 0 } zone })
                date.VenueTimeZoneId = zone;
        }

        foreach (var type in graph.Types.Where(t => t.CapacityPerDate is not null && AppliesTo(t, date)))
            await EnsureQuotaAsync(TicketQuotas.TypeOnDate(date.Id!, type.Id!), type.CapacityPerDate!.Value, shards, cancellationToken);
        var allDates = graph.Dates.Select(d => d.Id!).ToList();
        foreach (var phase in graph.Phases.Where(p => p.CapacityPerDate is not null && SaleWindows.PhaseDates(p, allDates).Contains(date.Id!)))
            await EnsureQuotaAsync(TicketQuotas.PhaseOnDate(date.Id!, phase.Id!), phase.CapacityPerDate!.Value, shards, cancellationToken);
        date.ProvisionedAt ??= clock.GetUtcNow();
    }

    internal static bool AppliesTo(TicketType type, TicketEventDate date) => type.DateIds.Count == 0 || type.DateIds.Contains(date.Id!);

    /// <summary>Creates the quota, or moves its capacity; refused (validation) below what is already sold and held.</summary>
    private async Task EnsureQuotaAsync(string quotaId, int capacity, int maxShards, CancellationToken cancellationToken)
    {
        await inventory.CreateQuotaAsync(quotaId, capacity, TicketQuotas.Shards(capacity, maxShards), cancellationToken);
        var availability = await inventory.GetAvailabilityAsync(quotaId, cancellationToken);
        if (availability.Capacity == capacity)
            return;
        var result = await inventory.SetCapacityAsync(quotaId, capacity, cancellationToken);
        if (!result.Succeeded)
            throw new SparkValidationException($"The capacity cannot go below the {availability.Sold + availability.Held} places already sold or held.", "Capacity");
    }
}
