using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Interceptors;
using MintPlayer.Spark.Commerce.Pricing;
using MintPlayer.Spark.Commerce.Ticketing.Events.Models;
using MintPlayer.Spark.Commerce.Ticketing.Events.Services;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents.Session;
using static MintPlayer.Spark.Commerce.Tests.Events.EventFixtures;

namespace MintPlayer.Spark.Commerce.Tests.Events;

/// <summary>
/// GA3 for sale phases (item 10): a phase's price change and its price-history entries are one transaction. Red before
/// the fix: the interceptor recorded in <c>OnAfterSave</c> and committed the entries with a second
/// <c>SaveChangesAsync</c>, so a failed second commit left a price change without its history (and the Omnibus prior
/// price wrong).
/// </summary>
public class PhasePriceHistoryTests : SparkTestDriver
{
    [Fact]
    public async Task An_edited_price_is_recorded_in_the_save_session_before_the_commit()
    {
        await SeedAsync(Phase("TicketSalePhases/1-A", ("TicketTypes/1-A", "", 30m)));
        var history = new SessionPriceHistory();

        using (var session = Store.OpenAsyncSession())
        {
            var phase = (await session.LoadAsync<TicketSalePhase>("TicketSalePhases/1-A"))!;
            var before = Phase("TicketSalePhases/1-A", ("TicketTypes/1-A", "", 30m));
            phase.Prices[0].Price = new Money(25m, "EUR");

            await Interceptor(history).OnBeforeSaveAsync(phase, Context(session, phase, before, PersistentObjectOperation.Save));

            history.Calls.Should().HaveCount(1);
            history.Calls[0].Session.Should().BeSameAs(session);
            history.Calls[0].Reason.Should().Be("edit");
            // Nothing committed yet: the entry waits for the save's own commit.
            using (var other = Store.OpenAsyncSession())
                (await other.Advanced.LoadStartingWithAsync<PriceMark>("PriceMarks/")).Should().BeEmpty();

            await session.SaveChangesAsync();
        }

        using var check = Store.OpenAsyncSession();
        (await check.Advanced.LoadStartingWithAsync<PriceMark>("PriceMarks/")).Should().HaveCount(1);
        (await check.LoadAsync<TicketSalePhase>("TicketSalePhases/1-A"))!.Prices[0].Price.Should().Be(new Money(25m, "EUR"));
    }

    [Fact]
    public async Task A_new_phase_gets_its_id_and_first_entries_in_the_same_session()
    {
        await SeedAsync();
        var history = new SessionPriceHistory();

        using var session = Store.OpenAsyncSession();
        var phase = Phase(null!, ("TicketTypes/1-A", "", 30m));
        phase.Id = null;

        await Interceptor(history).OnBeforeSaveAsync(phase, Context(session, phase, before: null, PersistentObjectOperation.New));

        phase.Id.Should().NotBeNullOrEmpty();
        history.Calls.Should().HaveCount(1);
        history.Calls[0].Reason.Should().Be("create");
        history.Calls[0].ItemId.Should().Be(Ticketing.Events.Sales.TicketItemIds.PriceItemId(phase.Id!, "TicketTypes/1-A", ""));

        // The pipeline stores the entity again (a no-op for a tracked one) and commits once: phase and entry together.
        await session.StoreAsync(phase);
        await session.SaveChangesAsync();
        using var check = Store.OpenAsyncSession();
        (await check.LoadAsync<TicketSalePhase>(phase.Id)).Should().NotBeNull();
        (await check.Advanced.LoadStartingWithAsync<PriceMark>("PriceMarks/")).Should().HaveCount(1);
    }

    [Fact]
    public async Task An_unchanged_price_records_nothing()
    {
        await SeedAsync(Phase("TicketSalePhases/1-A", ("TicketTypes/1-A", "", 30m)));
        var history = new SessionPriceHistory();
        using var session = Store.OpenAsyncSession();
        var phase = (await session.LoadAsync<TicketSalePhase>("TicketSalePhases/1-A"))!;

        await Interceptor(history).OnBeforeSaveAsync(phase, Context(session, phase, Phase("TicketSalePhases/1-A", ("TicketTypes/1-A", "", 30m)), PersistentObjectOperation.Save));

        history.Calls.Should().BeEmpty();
    }

    private async Task SeedAsync(TicketSalePhase? phase = null)
    {
        using var session = Store.OpenAsyncSession();
        var ev = Event();
        ev.IsPublished = false; // no re-provisioning: only the price history is under test
        await session.StoreAsync(ev);
        await session.StoreAsync(Type("TicketTypes/1-A"));
        if (phase is not null)
            await session.StoreAsync(phase);
        await session.SaveChangesAsync();
    }

    private static TicketSalePhaseInterceptor Interceptor(ISparkPriceHistory history)
    {
        var commerce = Monitor(CommerceOptions());
        // The publisher is only reached when a published event's capacity changes; these events are unpublished.
        var publisher = new TicketEventPublisher(null!, null!, null!, null!, commerce, Monitor(new Ticketing.Events.SparkTicketingEventsOptions()), TimeProvider.System);
        return new TicketSalePhaseInterceptor(publisher, history, new FinalPriceCalculator(commerce));
    }

    private static SaveContext Context(IAsyncDocumentSession session, TicketSalePhase phase, TicketSalePhase? before, PersistentObjectOperation operation) => new()
    {
        EntityType = typeof(TicketSalePhase),
        Operation = operation,
        PersistentObject = new PersistentObject { Id = phase.Id, Name = nameof(TicketSalePhase), ObjectTypeId = Guid.NewGuid() },
        Entity = phase,
        Before = before,
        Session = session,
    };

    private sealed class PriceMark
    {
        public string? Id { get; set; }
        public string ItemId { get; set; } = "";
    }

    /// <summary>Records each call and writes a marker document into the caller's session, as the real history does.</summary>
    private sealed class SessionPriceHistory : ISparkPriceHistory
    {
        public List<(IAsyncDocumentSession Session, string ItemId, string Reason)> Calls { get; } = [];

        public async Task<PriceHistoryEntry> RecordAsync(IAsyncDocumentSession session, ISellableItem item, Money? previous, string reason,
            bool isAnnouncedReduction = false, CancellationToken cancellationToken = default)
        {
            Calls.Add((session, item.Id!, reason));
            await session.StoreAsync(new PriceMark { ItemId = item.Id! }, $"PriceMarks/{Calls.Count}", cancellationToken);
            return new PriceHistoryEntry(item.Id!, DateTimeOffset.UnixEpoch, previous, item.Price!, item.Price!, reason, null, null);
        }

        public Task<Money?> GetLowestFinalPriceAsync(string itemId, DateTimeOffset before, TimeSpan? window = null, CancellationToken cancellationToken = default)
            => Task.FromResult<Money?>(null);
    }
}
