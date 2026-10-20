using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Commerce.Checkout.Models;
using MintPlayer.Spark.Commerce.Checkout.Refunds;
using MintPlayer.Spark.Commerce.Inventory;
using MintPlayer.Spark.Commerce.Tests.Events;
using MintPlayer.Spark.Commerce.Ticketing.Events.Sales;
using MintPlayer.Spark.Commerce.Ticketing.Tickets;
using MintPlayer.Spark.Commerce.Ticketing.Tickets.Models;
using MintPlayer.Spark.Commerce.Ticketing.Tickets.Secrets;
using MintPlayer.Spark.Commerce.Ticketing.Tickets.Services;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents;
using static MintPlayer.Spark.Commerce.Tests.Events.EventFixtures;
using static MintPlayer.Spark.Commerce.Tests.Tickets.TicketFixtures;

namespace MintPlayer.Spark.Commerce.Tests.Tickets;

/// <summary>
/// The ticket lifecycle that follows the order (§4.11, §4.4, I4): issuance exactly once per line with PBKDF2 verifiers
/// and the seller's lead time (D11); revocation exactly once per revocation id; the dispute freeze, its release and the
/// revocation of a lost dispute (GB1); a seat change or upgrade reissues with a new secret (GB7); the holder data is
/// erased after the retention and on account deletion (§4.11 round 5, §4.15).
/// </summary>
public class TicketIssuanceTests : SparkTestDriver
{
    private readonly EventTestClock clock = new(Doors.AddDays(-20));
    private readonly SparkTicketingTicketsOptions options = Options();
    private readonly TicketSecretVault vault = new(new EphemeralDataProtectionProvider());

    private TicketIssuer Issuer() => new(Store, vault, new NoVenues(), new InventoryEngine(Store), Monitor(options), clock, NullLogger<TicketIssuer>.Instance);

    private static CheckoutOrder Order(string dateId, int quantity = 2, OrderState state = OrderState.Confirmed, string id = "CheckoutOrders/m15-1") => new()
    {
        Id = id, Number = "M15-1", State = state, BuyerUserId = "Users/buyer", BuyerEmail = "buyer@example.test", BuyerName = "Buyer",
        Lines =
        [
            new CheckoutOrderLine
            {
                LineId = "L1", ItemId = TicketItemIds.For("TicketSalePhases/1", "TicketTypes/standard", "", dateId), Channel = "tickets",
                Quantity = quantity, Status = OrderLineStatus.Active, ClaimId = "claims/m15-1/L1", SellerOrganizationId = Organization,
            },
        ],
    };

    private async Task<List<Ticket>> TicketsAsync(string orderId)
    {
        using var session = Store.OpenAsyncSession();
        return await session.Query<Ticket>().Customize(c => c.WaitForNonStaleResults()).Where(t => t.OrderId == orderId).OrderBy(t => t.Position).ToListAsync();
    }

    private async Task<string?> SecretAsync(Ticket ticket)
    {
        using var session = Store.OpenAsyncSession();
        return vault.Unprotect(await session.LoadAsync<TicketSecret>(TicketSecret.IdFor(ticket.Code)), ticket.Code, ticket.Generation);
    }

    [Fact]
    public async Task A_line_is_issued_once_one_ticket_per_unit_with_a_PBKDF2_verifier()
    {
        var (dates, _, _) = await SeedEventAsync(Store);
        var order = Order(dates[0]);

        (await Issuer().IssueAsync(order, CancellationToken.None)).Should().HaveCount(2);
        (await Issuer().IssueAsync(order, CancellationToken.None)).Should().BeEmpty();

        var tickets = await TicketsAsync(order.Id!);
        tickets.Should().HaveCount(2);
        tickets.Select(t => t.Position).Should().Equal(1, 2);
        foreach (var ticket in tickets)
        {
            ticket.SecretIterations.Should().Be(TestIterations);
            ticket.Status.Should().Be(ETicketStatus.Valid);
            ticket.HolderUserId.Should().Be("Users/buyer");
            ticket.HolderName.Should().Be("Buyer");
            ticket.DeliveryDue.Should().BeTrue();
            ticket.AvailableFrom.HasValue.Should().BeFalse();
            ticket.OrganizationId.Should().Be(Organization);
            var secret = await SecretAsync(ticket);
            secret.Should().NotBeNull();
            TicketSecrets.Verify(ticket, secret).Should().BeTrue();
        }
    }

    [Fact]
    public async Task D11_tickets_appear_at_the_sellers_lead_time()
    {
        var (dates, _, _) = await SeedEventAsync(Store);
        using (var session = Store.OpenAsyncSession())
        {
            await session.StoreAsync(new TicketEventSettings { EventId = EventId, OrganizationId = Organization, DeliveryLeadTimeHours = 48, NamedTickets = true });
            await session.SaveChangesAsync();
        }
        await Issuer().IssueAsync(Order(dates[0], quantity: 1), CancellationToken.None);

        var ticket = (await TicketsAsync("CheckoutOrders/m15-1")).Single();
        var startsAt = Doors.AddHours(1);
        ticket.AvailableFrom.HasValue.Should().BeTrue();
        ticket.AvailableFrom!.Value.Should().Be(startsAt.AddHours(-48));
        ticket.DeliverAt.Should().Be(startsAt.AddHours(-48));
        // Named tickets: the buyer names each holder; the order's name is not printed on them.
        ticket.HolderName.Should().BeNull();
        ticket.FreezeAt.HasValue.Should().BeTrue();
        ticket.FreezeAt!.Value.Should().Be(Doors - options.Exchange.DefaultFreezeBeforeDoors);
    }

    [Fact]
    public async Task I4_a_revocation_applies_once_highest_positions_first()
    {
        var (dates, _, _) = await SeedEventAsync(Store);
        var order = Order(dates[0], quantity: 3);
        await Issuer().IssueAsync(order, CancellationToken.None);
        var revocation = new OrderTicketRevocation("refund-1", [new RevokedLine("L1", order.Lines[0].ItemId, 1, [])]);

        (await Issuer().RevokeAsync(order, revocation, CancellationToken.None)).Should().Be(1);
        (await Issuer().RevokeAsync(order, revocation, CancellationToken.None)).Should().Be(0);

        var tickets = await TicketsAsync(order.Id!);
        tickets.Single(t => t.Status == ETicketStatus.Revoked).Position.Should().Be(3);
        tickets.Count(t => t.Status == ETicketStatus.Valid).Should().Be(2);
    }

    [Fact]
    public async Task GB1_a_dispute_freezes_unused_tickets_a_win_restores_them_a_loss_revokes_them()
    {
        var (dates, _, _) = await SeedEventAsync(Store);
        var order = Order(dates[0]);
        await Issuer().IssueAsync(order, CancellationToken.None);

        await Issuer().FreezeAsync(order, "dispute-1", CancellationToken.None);
        (await TicketsAsync(order.Id!)).Should().OnlyContain(t => t.Status == ETicketStatus.OnHold && t.HoldDisputeIds.Contains("dispute-1"));

        await Issuer().UnfreezeAsync(order, "dispute-1", CancellationToken.None);
        (await TicketsAsync(order.Id!)).Should().OnlyContain(t => t.Status == ETicketStatus.Valid && t.HoldDisputeIds.Count == 0);

        await Issuer().FreezeAsync(order, "dispute-2", CancellationToken.None);
        order.Lines[0].Status = OrderLineStatus.Returned;
        await Issuer().RevokeLostAsync(order, "dispute-2", CancellationToken.None);
        await Issuer().RevokeLostAsync(order, "dispute-2", CancellationToken.None);
        var lost = await TicketsAsync(order.Id!);
        lost.Should().OnlyContain(t => t.Status == ETicketStatus.Revoked);
        lost.Should().OnlyContain(t => t.RevocationIds.Count(r => r == "dispute:dispute-2") == 1);
    }

    [Fact]
    public async Task GB1_an_order_confirmed_while_disputed_issues_on_hold()
    {
        var (dates, _, _) = await SeedEventAsync(Store);
        var order = Order(dates[0], quantity: 1, state: OrderState.Disputed);
        order.Disputes.Add(new CheckoutOrderDispute { DisputeId = "d-open", OpenedAt = clock.Now });
        await Issuer().IssueAsync(order, CancellationToken.None);

        var ticket = (await TicketsAsync(order.Id!)).Single();
        ticket.Status.Should().Be(ETicketStatus.OnHold);
        ticket.HoldDisputeIds.Should().Equal("d-open");
    }

    [Fact]
    public async Task GB7_a_seat_change_reissues_with_a_new_secret_once_per_change()
    {
        var (dates, _, _) = await SeedEventAsync(Store);
        var order = Order(dates[0], quantity: 1);
        await Issuer().IssueAsync(order, CancellationToken.None);
        var before = (await TicketsAsync(order.Id!)).Single();
        var oldSecret = await SecretAsync(before);

        order.Lines[0].ClaimId = "claims/m15-1/L1/chg1";
        (await Issuer().ApplyLineChangeAsync(order, "chg1", CancellationToken.None)).Should().Equal(before.Code);
        (await Issuer().ApplyLineChangeAsync(order, "chg1", CancellationToken.None)).Should().BeEmpty();

        var after = (await TicketsAsync(order.Id!)).Single();
        after.Generation.Should().Be(2);
        after.SourceClaimId.Should().Be("claims/m15-1/L1/chg1");
        after.DeliveryDue.Should().BeTrue();
        TicketSecrets.Verify(after, oldSecret).Should().BeFalse();
        TicketSecrets.Verify(after, await SecretAsync(after)).Should().BeTrue();
    }

    [Fact]
    public async Task The_holders_data_is_erased_after_the_retention_and_on_account_deletion()
    {
        var (dates, _, _) = await SeedEventAsync(Store);
        await Issuer().IssueAsync(Order(dates[0], quantity: 1), CancellationToken.None);
        await Issuer().IssueAsync(Order(dates[0], quantity: 1, id: "CheckoutOrders/m15-2"), CancellationToken.None);

        // Account deletion: the holder of order 2's ticket deletes their account.
        using (var session = Store.OpenAsyncSession())
        {
            var t2 = (await session.Query<Ticket>().Customize(c => c.WaitForNonStaleResults()).Where(t => t.OrderId == "CheckoutOrders/m15-2").ToListAsync()).Single();
            t2.HolderUserId = "Users/leaver";
            await session.SaveChangesAsync();
        }
        await new TicketHolderAccountDeletion<MintPlayer.Spark.Authorization.Identity.SparkUser>(Store, clock)
            .OnDeletingAccountAsync(new MintPlayer.Spark.Authorization.Identity.SparkUser { Id = "Users/leaver" }, CancellationToken.None);
        var erased = (await TicketsAsync("CheckoutOrders/m15-2")).Single();
        erased.HolderUserId.Should().BeNull();
        erased.HolderEmail.Should().BeNull();
        erased.Status.Should().Be(ETicketStatus.Valid);

        // Retention: the last date's end plus HolderRetention.
        var job = new TicketHolderRetentionJob(Store, clock, NullLogger<TicketHolderRetentionJob>.Instance);
        await job.RunAsync(CancellationToken.None);
        (await TicketsAsync("CheckoutOrders/m15-1")).Single().HolderEmail.Should().Be("buyer@example.test");
        clock.Now = Doors.AddHours(7) + options.HolderRetention + TimeSpan.FromMinutes(1);
        await job.RunAsync(CancellationToken.None);
        var retained = (await TicketsAsync("CheckoutOrders/m15-1")).Single();
        retained.HolderName.Should().BeNull();
        retained.HolderEmail.Should().BeNull();
        retained.HolderUserId.Should().BeNull();
    }
}
