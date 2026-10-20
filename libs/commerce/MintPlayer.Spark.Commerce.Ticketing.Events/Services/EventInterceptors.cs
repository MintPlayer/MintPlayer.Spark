using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Interceptors;
using MintPlayer.Spark.Commerce.Ticketing.Events.Models;
using MintPlayer.Spark.Commerce.Ticketing.Events.Sales;
using MintPlayer.Spark.Services;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Commerce.Ticketing.Events.Services;

/// <summary>
/// Saves of an event through the persistent-object pipeline: the slug (from the name when empty, unique), the
/// timestamps, a channel that never changes once the event was published, and publication (X8, §4.11): when
/// <c>IsPublished</c> turns on, <see cref="TicketEventPublisher.PublishAsync"/> refuses an event that cannot sell and
/// provisions its dates. The organization's <c>Publish</c> role is Organizations' own gate (Finalize stage).
/// An event that was ever published is never deleted (its orders and tickets name it): unpublish it, or cancel it.
/// </summary>
internal sealed class TicketEventInterceptor(TicketEventPublisher publisher, TimeProvider clock)
    : IBeforeSave<TicketEvent>, IBeforeDelete<TicketEvent>
{
    public async ValueTask OnBeforeSaveAsync(TicketEvent entity, SaveContext context)
    {
        var session = context.GetSession();
        var before = context.Before as TicketEvent;
        var now = clock.GetUtcNow();
        if (before is null)
            entity.CreatedAt = now;
        entity.UpdatedAt = now;

        if (string.IsNullOrWhiteSpace(entity.Slug))
            entity.Slug = TicketEventPublisher.Slugify(entity.Name.GetValue(TranslatedString.DefaultLanguage));
        entity.Slug = entity.Slug.Trim().ToLowerInvariant();
        if (!TicketEventPublisher.IsSlug(entity.Slug))
            throw new SparkValidationException("Use lower-case letters, digits and dashes.", nameof(TicketEvent.Slug));
        if (before is null || before.Slug != entity.Slug)
        {
            var id = entity.Id;
            var slug = entity.Slug;
            if (await session.Query<TicketEvent>().Customize(c => c.WaitForNonStaleResults()).AnyAsync(e => e.Slug == slug && e.Id != id))
                throw new SparkValidationException("Another event uses this address.", nameof(TicketEvent.Slug));
        }

        if (before?.PublishedAt is not null && !string.Equals(before.Channel, entity.Channel, StringComparison.Ordinal))
            throw new SparkValidationException("The channel of an event that was published cannot change.", nameof(TicketEvent.Channel));
        entity.PublishedAt ??= before?.PublishedAt;

        if (entity.IsPublished && before?.IsPublished != true)
        {
            if (string.IsNullOrEmpty(entity.Id))
                throw new SparkValidationException("Save the event with its dates, ticket types and phases first, then publish it.", nameof(TicketEvent.IsPublished));
            await publisher.PublishAsync(session, entity, CancellationToken.None);
        }
    }

    public ValueTask OnBeforeDeleteAsync(TicketEvent entity, DeleteContext context)
    {
        if (entity.IsPublished || entity.PublishedAt is not null)
            throw new SparkValidationException("An event that was published is not deleted: unpublish or cancel it.", nameof(TicketEvent.IsPublished));
        return ValueTask.CompletedTask;
    }
}

/// <summary>Saves of dates (see <see cref="EventChildInterceptor"/>): a frozen layout never changes; a moved start keeps the previous one (schema.org <c>previousStartDate</c>).</summary>
internal sealed class TicketEventDateInterceptor(TicketEventPublisher publisher)
    : EventChildInterceptor(publisher), IBeforeSave<TicketEventDate>, IAfterSave<TicketEventDate>, IBeforeDelete<TicketEventDate>
{
    public async ValueTask OnBeforeSaveAsync(TicketEventDate entity, SaveContext context)
    {
        var before = context.Before as TicketEventDate;
        var ev = await AdoptAsync(context, entity.EventId, before?.EventId);
        entity.OrganizationId = ev.OrganizationId;
        if (before is not null)
        {
            entity.LayoutSnapshotId = before.LayoutSnapshotId;
            entity.ProvisionedAt = before.ProvisionedAt;
            if (before.LayoutSnapshotId is not null && (!string.Equals(before.LayoutId, entity.LayoutId, StringComparison.Ordinal) || before.LayoutVersion != entity.LayoutVersion))
                throw new SparkValidationException("The layout of a date on sale is frozen.", nameof(TicketEventDate.LayoutId));
            if (before.StartsAt != entity.StartsAt && before.ProvisionedAt is not null)
                entity.PreviousStartsAt ??= before.StartsAt;
        }
        if (entity.VenueTimeZoneId is { Length: > 0 } zone && !SparkTimeZones.IsKnown(zone))
            throw new SparkValidationException("Unknown time zone.", nameof(TicketEventDate.VenueTimeZoneId));
        if (ev.IsPublished && before?.ProvisionedAt is not null && string.IsNullOrEmpty(entity.LayoutId) && before.Capacity != entity.Capacity)
            await ProvisionAsync(context.GetSession(), ev, replaced: entity);
    }

    public ValueTask OnAfterSaveAsync(TicketEventDate entity, SaveContext context) => ProvisionAfterAsync(context, entity.EventId);

    public ValueTask OnBeforeDeleteAsync(TicketEventDate entity, DeleteContext context) => RefuseDeleteAsync(context, entity.EventId);
}

/// <summary>Saves of ticket types (see <see cref="EventChildInterceptor"/>); the attendee questions are checked here (GB3).</summary>
internal sealed class TicketTypeInterceptor(TicketEventPublisher publisher)
    : EventChildInterceptor(publisher), IBeforeSave<TicketType>, IAfterSave<TicketType>, IBeforeDelete<TicketType>
{
    public async ValueTask OnBeforeSaveAsync(TicketType entity, SaveContext context)
    {
        var before = context.Before as TicketType;
        var ev = await AdoptAsync(context, entity.EventId, before?.EventId);
        entity.OrganizationId = ev.OrganizationId;
        if (Answers.AttendeeQuestionRules.Problems(entity).FirstOrDefault() is { } problem)
            throw new SparkValidationException(problem, nameof(TicketType.Questions));
        if (before is not null && before.Kind != entity.Kind && ev.PublishedAt is not null)
            throw new SparkValidationException("The kind of a ticket type that was on sale cannot change.", nameof(TicketType.Kind));
        if (ev.IsPublished && before is not null && before.CapacityPerDate != entity.CapacityPerDate)
            await ProvisionAsync(context.GetSession(), ev, replaced: entity);
    }

    public ValueTask OnAfterSaveAsync(TicketType entity, SaveContext context) => ProvisionAfterAsync(context, entity.EventId);

    public ValueTask OnBeforeDeleteAsync(TicketType entity, DeleteContext context) => RefuseDeleteAsync(context, entity.EventId);
}

/// <summary>Saves of sale phases (see <see cref="EventChildInterceptor"/>): the presale code hash (D17) and the price history (GA3).</summary>
internal sealed class TicketSalePhaseInterceptor(TicketEventPublisher publisher, ISparkPriceHistory priceHistory, IFinalPriceCalculator calculator)
    : EventChildInterceptor(publisher), IBeforeSave<TicketSalePhase>, IAfterSave<TicketSalePhase>, IBeforeDelete<TicketSalePhase>
{
    public async ValueTask OnBeforeSaveAsync(TicketSalePhase entity, SaveContext context)
    {
        var before = context.Before as TicketSalePhase;
        var ev = await AdoptAsync(context, entity.EventId, before?.EventId);
        entity.OrganizationId = ev.OrganizationId;

        // D17: the presale code is kept as a hash only. An empty field keeps the stored code; "-" removes it.
        if (entity.PresaleCode is { Length: > 0 } code)
            entity.PresaleCodeHash = code.Trim() == "-" ? null : SaleWindows.HashPresaleCode(code);
        else
            entity.PresaleCodeHash = before?.PresaleCodeHash ?? entity.PresaleCodeHash;
        entity.PresaleCode = null;

        foreach (var price in entity.Prices)
            price.Category = price.Category?.Trim() ?? "";
        if (entity.Prices.Select(p => p.Price.Currency).Distinct(StringComparer.Ordinal).Count() > 1)
            throw new SparkValidationException("Every price of a phase uses one currency.", nameof(TicketSalePhase.Prices));
        if (ev.IsPublished && before is not null && before.CapacityPerDate != entity.CapacityPerDate)
            await ProvisionAsync(context.GetSession(), ev, replaced: entity);

        await RecordPriceHistoryAsync(context.GetSession(), ev, entity, before);
    }

    /// <summary>
    /// GA3: every changed price of the phase goes into the price history (per phase × type × category), in the save's
    /// own session, so the entries commit with the price change or not at all. A new phase has no id yet: storing it
    /// here lets the session assign the id the pipeline then keeps (storing a tracked entity again is a no-op).
    /// </summary>
    private async Task RecordPriceHistoryAsync(IAsyncDocumentSession session, TicketEvent ev, TicketSalePhase entity, TicketSalePhase? before)
    {
        var changed = entity.Prices
            .Select(price => (price, previous: before?.Prices.FirstOrDefault(p => p.TicketTypeId == price.TicketTypeId && (p.Category ?? "") == price.Category)))
            .Where(c => c.previous is null || c.previous.Price != c.price.Price)
            .ToList();
        if (changed.Count == 0)
            return;
        if (string.IsNullOrEmpty(entity.Id))
            await session.StoreAsync(entity);
        foreach (var (price, previous) in changed)
        {
            var type = await session.LoadAsync<TicketType>(price.TicketTypeId);
            Money? previousFinal = previous is null
                ? null
                : calculator.Calculate(FinalPriceRequest.For(TicketPriceItem.For(ev, entity, previous, type))).UnitTotal;
            await priceHistory.RecordAsync(session, TicketPriceItem.For(ev, entity, price, type), previousFinal, previous is null ? "create" : "edit");
        }
    }

    public ValueTask OnAfterSaveAsync(TicketSalePhase entity, SaveContext context) => ProvisionAfterAsync(context, entity.EventId);

    public ValueTask OnBeforeDeleteAsync(TicketSalePhase entity, DeleteContext context) => RefuseDeleteAsync(context, entity.EventId);
}

/// <summary>
/// The shared rules of an event's dates, ticket types and phases: each takes its event's organization (X8: one row rule,
/// no join), never moves to another event, and, once the event is published, keeps selling correctly:
/// <list type="bullet">
/// <item>a smaller capacity is refused below what is already sold and held (before the save);</item>
/// <item>new dates, types and phases are provisioned after the save (quotas, snapshots);</item>
/// <item>a seated date never changes its layout once frozen.</item>
/// </list>
/// Children of an event that was ever published are not deleted (orders and tickets name them): end a phase instead.
/// One class per type: an interceptor implements the typed interfaces of one entity type only.
/// </summary>
internal abstract class EventChildInterceptor(TicketEventPublisher publisher)
{
    /// <summary>The child's event: it must exist and the child never moves to another one.</summary>
    protected static async Task<TicketEvent> AdoptAsync(SaveContext context, string eventId, string? previousEventId)
    {
        if (previousEventId is not null && !string.Equals(previousEventId, eventId, StringComparison.Ordinal))
            throw new SparkValidationException("A date, ticket type or phase never moves to another event.", "EventId");
        if (string.IsNullOrEmpty(eventId) || await context.GetSession().LoadAsync<TicketEvent>(eventId) is not { } ev)
            throw new SparkValidationException("Choose the event.", "EventId");
        return ev;
    }

    /// <summary>Re-provisions every date of a published event, with <paramref name="replaced"/> as it will be saved.</summary>
    protected async Task ProvisionAsync(IAsyncDocumentSession session, TicketEvent ev, object replaced)
    {
        var graph = await TicketEventGraph.LoadAsync(session, ev, CancellationToken.None);
        graph = replaced switch
        {
            TicketEventDate date => graph with { Dates = graph.Dates.Select(d => d.Id == date.Id ? date : d).ToList() },
            TicketType type => graph with { Types = graph.Types.Select(t => t.Id == type.Id ? type : t).ToList() },
            TicketSalePhase phase => graph with { Phases = graph.Phases.Select(p => p.Id == phase.Id ? phase : p).ToList() },
            _ => graph,
        };
        var liveUntil = TicketEventPublisher.LiveUntil(graph);
        foreach (var date in graph.Dates.Where(d => d.ProvisionedAt is not null || ReferenceEquals(d, replaced)))
            await publisher.ProvisionDateAsync(session, date, graph, liveUntil, CancellationToken.None);
    }

    /// <summary>After a child's save: a published event provisions what is new (a date, a capped type or phase).</summary>
    protected async ValueTask ProvisionAfterAsync(SaveContext context, string eventId)
    {
        var session = context.GetSession();
        var ev = await session.LoadAsync<TicketEvent>(eventId);
        if (ev is not { IsPublished: true })
            return;
        var graph = await TicketEventGraph.LoadAsync(session, ev, CancellationToken.None);
        var liveUntil = TicketEventPublisher.LiveUntil(graph);
        foreach (var date in graph.Dates)
            await publisher.ProvisionDateAsync(session, date, graph, liveUntil, CancellationToken.None);
        await session.SaveChangesAsync();
    }

    protected static async ValueTask RefuseDeleteAsync(DeleteContext context, string eventId)
    {
        var ev = await context.GetSession().LoadAsync<TicketEvent>(eventId);
        if (ev?.PublishedAt is not null)
            throw new SparkValidationException("Dates, ticket types and phases of an event that was published are not deleted: end the phase or cancel the date instead.");
    }
}
