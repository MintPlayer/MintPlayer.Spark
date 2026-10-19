using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Commerce.Checkout;
using MintPlayer.Spark.Commerce.Checkout.Models;
using MintPlayer.Spark.Commerce.Inventory;
using MintPlayer.Spark.Commerce.Ticketing.Events;
using MintPlayer.Spark.Commerce.Ticketing.Events.Answers;
using MintPlayer.Spark.Commerce.Ticketing.Events.Contracts;
using MintPlayer.Spark.Commerce.Ticketing.Events.Models;
using MintPlayer.Spark.Commerce.Ticketing.Events.PreSelect;
using MintPlayer.Spark.Commerce.Ticketing.Events.Sales;
using MintPlayer.Spark.Commerce.Ticketing.Events.Services;
using MintPlayer.Spark.Testing;
using static MintPlayer.Spark.Commerce.Tests.Events.EventFixtures;

namespace MintPlayer.Spark.Commerce.Tests.Events;

/// <summary>
/// Ticketing events against RavenDB and the real inventory engine (M13): publication refuses an event that cannot sell
/// and provisions its quotas; ticket types sell through the capacity source; attendee answers are stored per ticket
/// position, closed at the deadline, exported without sensitive answers and erased after the retention (GB3);
/// PreSelectOrder holds and releases through checkout (D25).
/// </summary>
public class EventFlowTests : SparkTestDriver
{
    private readonly EventTestClock clock = new(Now);
    private readonly OpenSellers sellers = new();
    private readonly InventoryEngine engine;
    private readonly SparkTicketingEventsOptions options = new();

    public EventFlowTests() => engine = new InventoryEngine(Store);

    private TicketEventPublisher Publisher() => new(engine, new NoVenues(), sellers, sellers, Monitor(CommerceOptions()), Monitor(options), clock);

    private TicketCapacitySource Source() => new(Store, new NoSeating(), Monitor(CommerceOptions()), clock);

    private sealed class NoSeating : ITicketSeating
    {
        public Task<global::MintPlayer.Spark.Commerce.Ticketing.Venues.Plans.SnapshotInventory?> GetAsync(string snapshotId, CancellationToken cancellationToken) => Task.FromResult<global::MintPlayer.Spark.Commerce.Ticketing.Venues.Plans.SnapshotInventory?>(null);
    }

    private sealed record Seeded(string EventId, string DateId, string TypeId, string PhaseId)
    {
        public string ItemId => TicketItemIds.For(PhaseId, TypeId, "", DateId);
    }

    private async Task<Seeded> SeedAsync(int capacity = 100, bool publish = true)
    {
        using var session = Store.OpenAsyncSession();
        var ev = Event(id: null!);
        ev.IsPublished = false;
        await session.StoreAsync(ev);
        var date = Date(null!, Now.AddDays(10), capacity, ev.Id!);
        await session.StoreAsync(date);
        var type = Type(null!, eventId: ev.Id!);
        type.Name = TranslatedString.Of("en", "Standard");
        type.Questions =
        [
            new AttendeeQuestion { Key = "name", Label = TranslatedString.Of("en", "Name"), Required = true },
            new AttendeeQuestion { Key = "diet", Label = TranslatedString.Of("en", "Diet"), Sensitive = true, ConsentText = TranslatedString.Of("en", "I consent to my diet being processed.") },
        ];
        await session.StoreAsync(type);
        var phase = Phase(null!, (type.Id!, "", 30m));
        phase.EventId = ev.Id!;
        phase.PreSelect = true;
        phase.QueueEnabled = true;
        await session.StoreAsync(phase);
        // The phase's price names the type's id, known only now.
        phase.Prices[0].TicketTypeId = type.Id!;
        await session.SaveChangesAsync();

        if (publish)
        {
            await Publisher().PublishAsync(session, ev, CancellationToken.None);
            ev.IsPublished = true;
            await session.SaveChangesAsync();
        }
        return new Seeded(ev.Id!, date.Id!, type.Id!, phase.Id!);
    }

    // ---- publication and selling --------------------------------------------------------------------------------

    [Fact]
    public async Task Publishing_provisions_the_date_quota_and_the_ticket_type_sells_through_it()
    {
        var seeded = await SeedAsync(capacity: 100);
        (await engine.GetAvailabilityAsync(TicketQuotas.Date(seeded.DateId))).Capacity.Should().Be(100);

        var item = await Source().ResolveAsync(new CheckoutItemRequest(seeded.ItemId, 2), CancellationToken.None);
        item.Should().NotBeNull();
        item!.AdmissionScope.Should().Be(TicketScopes.Admission(seeded.PhaseId));

        var claim = await engine.TryClaimAsync(new ClaimRequest("claims/test/1", "baskets/1", TimeProvider.System.GetUtcNow().AddMinutes(10), item.Capacity.Quotas));
        claim.Succeeded.Should().BeTrue();
        (await engine.GetAvailabilityAsync(TicketQuotas.Date(seeded.DateId))).Available.Should().Be(98);

        // Re-provisioning is idempotent, and a capacity below what is held is refused.
        using var session = Store.OpenAsyncSession();
        var ev = await session.LoadAsync<TicketEvent>(seeded.EventId);
        var graph = await TicketEventGraph.LoadAsync(session, ev, CancellationToken.None);
        graph.Dates[0].Capacity = 1;
        var shrink = () => Publisher().ProvisionDateAsync(session, graph.Dates[0], graph, Now.AddDays(10), CancellationToken.None);
        await shrink.Should().ThrowAsync<SparkValidationException>();
        graph.Dates[0].Capacity = 120;
        await Publisher().ProvisionDateAsync(session, graph.Dates[0], graph, Now.AddDays(10), CancellationToken.None);
        (await engine.GetAvailabilityAsync(TicketQuotas.Date(seeded.DateId))).Capacity.Should().Be(120);
    }

    [Fact]
    public async Task Publication_is_refused_when_the_seller_may_not_sell_or_a_legal_document_is_missing()
    {
        sellers.Gate = SellerGate.Refused("commerce.seller.notApproved");
        sellers.MissingDocuments.Add(LegalDocumentKinds.TermsOfSale);
        var publish = () => SeedAsync();
        var refused = await publish.Should().ThrowAsync<SparkValidationException>();
        refused.Which.Message.Should().Contain("commerce.seller.notApproved");
        refused.Which.Message.Should().Contain(LegalDocumentKinds.TermsOfSale);
    }

    [Fact]
    public async Task An_unpublished_event_does_not_sell()
    {
        var seeded = await SeedAsync(publish: false);
        (await Source().ResolveAsync(new CheckoutItemRequest(seeded.ItemId, 1), CancellationToken.None)).Should().BeNull();
    }

    // ---- attendee answers (GB3) ---------------------------------------------------------------------------------

    private static CheckoutOrder Order(Seeded seeded) => new()
    {
        Id = "CheckoutOrders/1-A",
        Number = "T-1",
        State = OrderState.Confirmed,
        BuyerUserId = "users/buyer",
        Lines = [new CheckoutOrderLine { LineId = "l1", ItemId = seeded.ItemId, Quantity = 2, Status = OrderLineStatus.Active }],
    };

    [Fact]
    public async Task Answers_are_stored_per_position_closed_at_the_deadline_exported_without_sensitive_ones_and_erased_after_retention()
    {
        var seeded = await SeedAsync();
        var checkout = new FakeCheckout { Order = Order(seeded) };
        var answers = new AttendeeAnswers(Store, checkout, Monitor(options), clock);

        var missing = () => answers.SaveAsync(new SparkAttendeeAnswersRequest("T-1", "l1", 1, [new("diet", "vegan")]));
        var error = await missing.Should().ThrowAsync<AttendeeAnswerException>();
        error.Which.Errors.Select(e => e.Code).Should().BeEquivalentTo(new[] { AttendeeQuestionRules.ConsentRequired, AttendeeQuestionRules.Required });

        var saved = (await answers.SaveAsync(new SparkAttendeeAnswersRequest("T-1", "l1", 1, [new("name", "Ann"), new("diet", "vegan", Consent: true)], "en")))!;
        var line = saved.Lines.Single();
        line.Positions.Should().HaveCount(2);
        line.Positions[0].Answers.Should().HaveCount(2);
        line.Positions[0].Answers.Single(a => a.QuestionKey == "diet").Consented.Should().BeTrue();
        line.Positions[1].Missing.Should().Equal("name");
        line.Open.Should().BeTrue();

        // Another buyer's order is not found.
        (await answers.GetAsync("T-2")).Should().BeNull();

        // GB14: the seller's export leaves the sensitive answer out.
        var rows = new List<IReadOnlyList<string?>>();
        await foreach (var row in new AttendeeAnswerExport(Store).GetRowsAsync("Organizations/seller", null, null))
            rows.Add(row);
        rows.Should().ContainSingle();
        rows[0][6].Should().Be("name");
        rows[0][7].Should().Be("Ann");

        // The deadline (2 hours before the start by default) closes the answers.
        clock.Now = Now.AddDays(10).AddHours(-1);
        var late = () => answers.SaveAsync(new SparkAttendeeAnswersRequest("T-1", "l1", 2, [new("name", "Bob")]));
        await late.Should().ThrowAsync<AttendeeAnswerException>();

        // 30 days after the date the retention job erases them.
        clock.Now = Now.AddDays(10 + 31);
        await new AttendeeAnswerRetentionJob(Store, clock, NullLogger<AttendeeAnswerRetentionJob>.Instance).RunAsync(CancellationToken.None);
        using var session = Store.OpenAsyncSession();
        (await session.LoadAsync<AttendeeAnswerSet>(AttendeeAnswerSet.IdFor("CheckoutOrders/1-A", "l1", 1))).Should().BeNull();
    }

    [Fact]
    public async Task A_cancelled_order_erases_its_answers()
    {
        var seeded = await SeedAsync();
        var checkout = new FakeCheckout { Order = Order(seeded) };
        var answers = new AttendeeAnswers(Store, checkout, Monitor(options), clock);
        await answers.SaveAsync(new SparkAttendeeAnswersRequest("T-1", "l1", 1, [new("name", "Ann")]));

        var order = Order(seeded);
        order.State = OrderState.Cancelled;
        await new AttendeeAnswerOrderEvents(answers).OnOrderEventAsync(new OrderEvent(order, OrderEventKind.Cancelled, OrderState.Confirmed), CancellationToken.None);

        using var session = Store.OpenAsyncSession();
        (await session.LoadAsync<AttendeeAnswerSet>(AttendeeAnswerSet.IdFor("CheckoutOrders/1-A", "l1", 1))).Should().BeNull();
    }

    // ---- PreSelectOrder (D25) -----------------------------------------------------------------------------------

    [Fact]
    public async Task A_preselection_is_held_through_checkout_at_admission_and_released_in_one_click()
    {
        var seeded = await SeedAsync();
        var checkout = new FakeCheckout { Refuse = request => request.Quantity > 2 ? CheckoutRefusals.LimitExceeded : null };
        var visitor = new FixedVisitor("users/fan");
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection()
            .AddSingleton<MintPlayer.Spark.Abstractions.Visitor.ISparkVisitor>(visitor)
            .BuildServiceProvider();
        var preSelect = new PreSelectOrders(Store, checkout, services, Monitor(options), clock);

        await preSelect.SaveAsync(new SparkPreSelectionRequest(seeded.PhaseId, [new SparkPreSelectLine([new SparkPreSelectChoice(seeded.ItemId, 4)])]));
        var applied = await preSelect.ApplyAsync(new SparkPreSelectApplyRequest(seeded.PhaseId, "token"));
        applied.Applied.Should().ContainSingle();
        applied.Applied[0].Quantity.Should().Be(2); // clamped to the buyer's limit
        checkout.Lines.Should().ContainSingle();

        // Applying again while the line is held answers the same.
        (await preSelect.ApplyAsync(new SparkPreSelectApplyRequest(seeded.PhaseId, "token"))).Applied.Should().ContainSingle();
        checkout.Lines.Should().ContainSingle();

        (await preSelect.ReleaseAsync(seeded.PhaseId))!.Applied.Should().BeEmpty();
        checkout.Lines.Should().BeEmpty();

        // An item of another phase is refused.
        var foreign = () => preSelect.SaveAsync(new SparkPreSelectionRequest(seeded.PhaseId, [new SparkPreSelectLine([new SparkPreSelectChoice("ticketing:other|t||d", 1)])]));
        await foreign.Should().ThrowAsync<CheckoutException>();
    }

    private sealed class FixedVisitor(string userId) : MintPlayer.Spark.Abstractions.Visitor.ISparkVisitor
    {
        public string? VisitorId => null;
        public string? UserId => userId;
        public string IdentityKey => "user:" + userId;
        public bool HasVisitorCookie => false;
        public ValueTask<string> EnsureAsync() => ValueTask.FromResult("visitor");
    }
}
