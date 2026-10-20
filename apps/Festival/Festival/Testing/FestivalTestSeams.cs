using Festival.Seed;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Commerce.Inventory;
using MintPlayer.Spark.Commerce.Ticketing.Events.Models;
using MintPlayer.Spark.Commerce.Ticketing.Events.Sales;
using MintPlayer.Spark.Commerce.WaitingRoom;
using MintPlayer.Spark.WaitingRoom;
using Raven.Client.Documents;

namespace Festival.Testing;

/// <summary>
/// End-to-end test seams for the festival demo (PRD §14). The E2E host runs the app out of process, so what the
/// libraries keep in process (the test provider's disputes and refund behaviour, the late webhooks) and what needs a
/// fresh schedule (a queued sale that opens in a few seconds) is reached over <c>/festival-test/*</c>.
/// </summary>
/// <remarks>
/// Off unless <c>Festival:TestSeams:Enabled</c> is true, which only Development and the E2E host's generated settings
/// set. Refused at startup in Production. Even when on, only a platform administrator (a holder of
/// <c>ManageSellers/Commerce</c>) gets an answer other than 404, and every call needs the antiforgery token.
/// </remarks>
public static class FestivalTestSeams
{
    public const string EnabledKey = "Festival:TestSeams:Enabled";

    /// <summary>Whether the seams are on. Throws when they are configured on in Production.</summary>
    public static bool IsEnabled(IConfiguration configuration, IHostEnvironment environment)
    {
        if (!configuration.GetValue<bool>(EnabledKey))
            return false;
        if (environment.IsProduction())
            throw new InvalidOperationException($"'{EnabledKey}' is set in Production. The festival test seams drive the test payment provider; they exist for the E2E host only.");
        return true;
    }
}

/// <summary>What <see cref="FestivalTestEvents.CreateQueuedAsync"/> made.</summary>
public sealed record FestivalQueuedEvent(string EventId, string Slug, string DateId, string TicketTypeId, string PhaseId, string ItemId, string? RoomKey);

/// <summary>
/// A queued sale made on request: one event with one general-admission date, one ticket type and one queued phase, in
/// the queue mode the test names, opening and starting at the offsets it names. Written outside the pipeline like the
/// seed, then provisioned (quota) and given its waiting room.
/// </summary>
public sealed class FestivalTestEvents(IDocumentStore store, ISparkInventory inventory, ISparkCommerceWaitingRooms rooms, TimeProvider clock)
{
    public async Task<FestivalQueuedEvent> CreateQueuedAsync(WaitingRoomMode mode, TimeSpan opensIn, TimeSpan startsIn, bool preSelect, int capacity, int perBuyerLimit, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var slug = "queued-sale-" + suffix;
        var eventId = "TicketEvents/" + slug;
        var dateId = "TicketEventDates/" + slug;
        var typeId = "TicketTypes/" + slug;
        var phaseId = "TicketSalePhases/" + slug;
        var startsAt = now.AddDays(30);
        var org = FestivalIds.Organization;

        using (var session = store.OpenAsyncSession())
        {
            await session.StoreAsync(new TicketEvent
            {
                OrganizationId = org,
                IsPublished = true,
                Name = TranslatedString.Of("en", $"Queued sale {suffix} ({mode})"),
                Slug = slug,
                Channel = "tickets",
                Policy = new TicketSalePolicy { PerBuyerLimit = perBuyerLimit, QueueMode = mode },
                PublishedAt = now,
                CreatedAt = now,
                UpdatedAt = now,
            }, eventId, cancellationToken);
            await session.StoreAsync(new TicketEventDate
            {
                OrganizationId = org, EventId = eventId, StartsAt = startsAt, DoorsOpenAt = startsAt.AddHours(-1), EndsAt = startsAt.AddHours(4),
                VenueId = FestivalIds.Venue, VenueTimeZoneId = FestivalSeedPlan.Zone, Capacity = capacity, ProvisionedAt = now,
            }, dateId, cancellationToken);
            await session.StoreAsync(new TicketType
            {
                OrganizationId = org, EventId = eventId, Name = TranslatedString.Of("en", "Standing"), Kind = ETicketTypeKind.Admission, VatRateBasisPoints = 600,
            }, typeId, cancellationToken);
            await session.StoreAsync(new TicketSalePhase
            {
                OrganizationId = org, EventId = eventId, Name = TranslatedString.Of("en", "Queued sale"),
                QueueEnabled = true, QueueOpensAt = now + opensIn, StartsAt = now + startsIn, EndsAt = startsAt, PreSelect = preSelect,
                Prices = [new TicketPhasePrice { TicketTypeId = typeId, Price = new Money(30m, FestivalSeedPlan.Currency) }],
            }, phaseId, cancellationToken);
            await session.SaveChangesAsync(cancellationToken);
        }

        await inventory.CreateQuotaAsync(TicketQuotas.Date(dateId), capacity, TicketQuotas.Shards(capacity, 16), cancellationToken);
        var roomKey = await rooms.SyncPhaseAsync(phaseId, cancellationToken);
        return new FestivalQueuedEvent(eventId, slug, dateId, typeId, phaseId, TicketItemIds.For(phaseId, typeId, "", dateId), roomKey);
    }
}
