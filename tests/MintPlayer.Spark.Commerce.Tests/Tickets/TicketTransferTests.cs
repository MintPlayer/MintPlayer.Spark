using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authentication;
using MintPlayer.Spark.Commerce.Inventory;
using MintPlayer.Spark.Commerce.Tests.Events;
using MintPlayer.Spark.Commerce.Ticketing.Events.Answers;
using MintPlayer.Spark.Commerce.Ticketing.Events.Contracts;
using MintPlayer.Spark.Commerce.Ticketing.Events.Models;
using MintPlayer.Spark.Commerce.Ticketing.Tickets;
using MintPlayer.Spark.Commerce.Ticketing.Tickets.Contracts;
using MintPlayer.Spark.Commerce.Ticketing.Tickets.Exchange;
using MintPlayer.Spark.Commerce.Ticketing.Tickets.Models;
using MintPlayer.Spark.Commerce.Ticketing.Tickets.Pdf;
using MintPlayer.Spark.Commerce.Ticketing.Tickets.Secrets;
using MintPlayer.Spark.Commerce.Ticketing.Tickets.Services;
using MintPlayer.Spark.MailManager;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents.Session;
using static MintPlayer.Spark.Commerce.Tests.Events.EventFixtures;
using static MintPlayer.Spark.Commerce.Tests.Tickets.TicketFixtures;

namespace MintPlayer.Spark.Commerce.Tests.Tickets;

/// <summary>
/// Transfer (D30) with the round-5 personal-data rule (§4.11): the new holder's data replaces the previous holder's, which
/// is erased from the ticket at once together with the attendee answers of that position (GB3); the ticket is reissued
/// with a new secret so the previous holder's link stops working; the audit records only between which accounts the
/// ticket moved. Transfers freeze before doors.
/// </summary>
public class TicketTransferTests : SparkTestDriver
{
    private readonly EventTestClock clock = new(Doors.AddDays(-5));
    private readonly SparkTicketingTicketsOptions options = Options();
    private readonly TicketSecretVault vault = new(new EphemeralDataProtectionProvider());
    private readonly CurrentUser user = new();
    private readonly RecordingAnswers answers = new();
    private readonly RecordingAudit audit = new();
    private readonly CapturingMailer mailer = new();

    private TicketExchange Exchange()
    {
        var services = new ServiceCollection().AddSingleton<ISparkMailer>(mailer).BuildServiceProvider();
        var commerce = CommerceOptions(Channel(c => { c.Resale.Transfer = true; c.Resale.FaceValueResale = true; }));
        var presenter = new TicketPresenter(services, vault, new NoVenues(), Monitor(commerce), clock);
        var mails = new TicketMails(services, presenter, new TicketPdfRenderer(), Monitor(options), NullLogger<TicketMails>.Instance);
        return new TicketExchange(Store, new TicketAccess(user, services), vault, mails, new InventoryEngine(Store), answers, new FlatPrices(), audit,
            new PendingResaleSettlement(clock), Monitor(commerce), Monitor(options), clock, NullLogger<TicketExchange>.Instance);
    }

    private TicketPresenter Presenter()
    {
        var services = new ServiceCollection().BuildServiceProvider();
        return new TicketPresenter(services, vault, new NoVenues(), Monitor(CommerceOptions()), clock);
    }

    private async Task<(Ticket Ticket, string Secret)> SeedHeldTicketAsync()
    {
        var (dates, _, _) = await SeedEventAsync(Store);
        using (var session = Store.OpenAsyncSession())
        {
            var ev = await session.LoadAsync<TicketEvent>(EventId);
            ev.Policy.Transfer = true;
            await session.SaveChangesAsync();
        }
        var (ticket, secret) = NewTicket(dates);
        ticket.HolderUserId = "Users/giver";
        ticket.HolderName = "Giver Person";
        ticket.HolderEmail = "giver@example.test";
        ticket.FreezeAt = Doors.AddHours(-2);
        using (var session = Store.OpenAsyncSession())
        {
            await session.StoreAsync(ticket, ticket.Id);
            await session.StoreAsync(vault.Protect(ticket.Code, secret, 1), TicketSecret.IdFor(ticket.Code));
            await session.SaveChangesAsync();
        }
        return (ticket, secret);
    }

    [Fact]
    public async Task Round5_accepting_a_transfer_reissues_the_ticket_and_erases_the_previous_holder()
    {
        var (ticket, oldSecret) = await SeedHeldTicketAsync();
        user.Id = "Users/giver";
        var info = await Exchange().StartTransferAsync(new SparkTicketTransferRequest(ticket.Code, "new@example.test", "New Holder"), CancellationToken.None);
        info.Should().NotBeNull();
        info!.Status.Should().Be(nameof(ETicketTransferStatus.Pending));
        var token = mailer.Token();

        user.Id = "Users/receiver";
        var view = await Exchange().AcceptAsync(new SparkTicketTransferAccept(info.TransferId, token), Presenter(), CancellationToken.None);
        view.Should().NotBeNull();
        view!.HolderName.Should().Be("New Holder");
        view.Generation.Should().Be(2);

        using var session = Store.OpenAsyncSession();
        var stored = await session.LoadAsync<Ticket>(ticket.Id);
        stored.HolderUserId.Should().Be("Users/receiver");
        stored.HolderEmail.Should().Be("new@example.test");
        stored.HolderName.Should().Be("New Holder");
        stored.AcquiredThrough.Should().Be("transfer:" + info.TransferId);
        // The previous holder's link stops working: a new secret.
        TicketSecrets.Verify(stored, oldSecret).Should().BeFalse();
        // The previous holder's answers of this position are erased (GB3).
        answers.Erased.Should().Equal(("CheckoutOrders/1", "L1", (int?)1));
        // The audit names the accounts, nothing else; the transfer forgets the recipient's address.
        audit.Entries.Single().Data!["from"].Should().Be("Users/giver");
        audit.Entries.Single().Data!["to"].Should().Be("Users/receiver");
        var transfer = await session.LoadAsync<TicketTransfer>(info.TransferId);
        transfer.Status.Should().Be(ETicketTransferStatus.Accepted);
        transfer.ToEmail.Should().BeNull();
        transfer.ToName.Should().BeNull();
    }

    [Fact]
    public async Task A_wrong_token_accepts_nothing_and_the_giver_no_longer_opens_the_ticket_after_acceptance()
    {
        var (ticket, _) = await SeedHeldTicketAsync();
        user.Id = "Users/giver";
        var info = await Exchange().StartTransferAsync(new SparkTicketTransferRequest(ticket.Code, "new@example.test"), CancellationToken.None);

        user.Id = "Users/receiver";
        (await Exchange().AcceptAsync(new SparkTicketTransferAccept(info!.TransferId, TicketSecrets.NewToken()), Presenter(), CancellationToken.None)).Should().BeNull();
        await Exchange().AcceptAsync(new SparkTicketTransferAccept(info.TransferId, mailer.Token()), Presenter(), CancellationToken.None);

        user.Id = "Users/giver";
        using var session = Store.OpenAsyncSession();
        (await new TicketAccess(user, new ServiceCollection().BuildServiceProvider()).HolderAsync(session, ticket.Code, null, CancellationToken.None)).HasValue.Should().BeFalse();
    }

    [Fact]
    public async Task Transfers_freeze_before_doors()
    {
        var (ticket, _) = await SeedHeldTicketAsync();
        user.Id = "Users/giver";
        clock.Now = Doors.AddHours(-1);
        var late = () => Exchange().StartTransferAsync(new SparkTicketTransferRequest(ticket.Code, "new@example.test"), CancellationToken.None);
        (await late.Should().ThrowAsync<TicketException>()).Which.Code.Should().Be(TicketRefusals.Frozen);
    }

    /// <summary>
    /// Red before the fix: with no listing yet, the page had no currency and listed in EUR, which the server refused for
    /// a GBP event; and the cap was the line's undiscounted total. The view now carries the cap (what the ticket cost,
    /// after the order discount, S21) in the order's currency.
    /// </summary>
    [Fact]
    public async Task The_resale_form_gets_the_events_currency_and_the_discounted_cap_before_any_listing()
    {
        var (ticket, secret) = await SeedHeldTicketAsync();
        using (var session = Store.OpenAsyncSession())
        {
            (await session.LoadAsync<TicketEvent>(EventId)).Policy.FaceValueResale = true;
            await session.StoreAsync(new MintPlayer.Spark.Commerce.Checkout.Models.CheckoutOrder
            {
                Id = "CheckoutOrders/1", Number = "T-1", Currency = "GBP",
                Lines =
                [
                    new MintPlayer.Spark.Commerce.Checkout.Models.CheckoutOrderLine
                    {
                        LineId = "L1", ItemId = "ticketing:test", Quantity = 1, Total = new Money(50m, "GBP"), OrderDiscount = new Money(-5m, "GBP"),
                    },
                ],
            }, "CheckoutOrders/1");
            await session.SaveChangesAsync();
        }
        user.Id = "Users/giver";
        var commerce = CommerceOptions(Channel(c => { c.Resale.Transfer = true; c.Resale.FaceValueResale = true; }));
        var presenter = new TicketPresenter(new ServiceCollection().BuildServiceProvider(), vault, new NoVenues(), Monitor(commerce), clock);

        using (var session = Store.OpenAsyncSession())
        {
            var view = await presenter.ViewAsync(session, (await session.LoadAsync<Ticket>(ticket.Id))!, secret, CancellationToken.None);
            view.CanResell.Should().BeTrue();
            view.Resale.Should().BeNull();
            view.ResaleCap.Should().Be(new Money(45m, "GBP"));
        }

        var above = () => Exchange().ListAsync(new SparkTicketResaleRequest(ticket.Code, new Money(50m, "GBP"), secret), CancellationToken.None);
        (await above.Should().ThrowAsync<TicketException>()).Which.Code.Should().Be(TicketRefusals.AboveFaceValue);
        var listed = await Exchange().ListAsync(new SparkTicketResaleRequest(ticket.Code, new Money(45m, "GBP"), secret), CancellationToken.None);
        listed!.Cap.Should().Be(new Money(45m, "GBP"));
    }

    private sealed class CurrentUser : ISparkCurrentUser
    {
        public string? Id { get; set; }

        public bool IsAuthenticated => Id is not null;
    }

    private sealed class RecordingAnswers : IAttendeeAnswers
    {
        public List<(string OrderId, string LineId, int? Position)> Erased { get; } = [];

        public Task<SparkAttendeeQuestionnaire?> GetAsync(string orderNumber, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<SparkAttendeeQuestionnaire?> SaveAsync(SparkAttendeeAnswersRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task EraseAsync(string orderId, string lineId, int? position = null, CancellationToken cancellationToken = default)
        {
            Erased.Add((orderId, lineId, position));
            return Task.CompletedTask;
        }

        public Task EraseOrderAsync(string orderId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class RecordingAudit : ICommerceAudit
    {
        public List<CommerceAuditEntry> Entries { get; } = [];

        public Task RecordAsync(IAsyncDocumentSession session, CommerceAuditEntry entry, CancellationToken cancellationToken = default)
        {
            Entries.Add(entry);
            return Task.CompletedTask;
        }
    }

    private sealed class FlatPrices : IFinalPriceCalculator
    {
        public FinalPrice Calculate(FinalPriceRequest request)
            => new(request.UnitPrice, Money.Zero(request.UnitPrice.Currency), request.UnitPrice, [], [], request.UnitPrice, request.UnitPrice);
    }

    /// <summary>Captures the transfer offer mail; the acceptance token travels in the link's fragment (<c>#t=</c>).</summary>
    private sealed class CapturingMailer : ISparkMailer
    {
        public List<SparkMailRequest> Sent { get; } = [];

        public string Token()
        {
            var data = Sent.Last().Data!;
            var url = (string)data.GetType().GetProperty("accept_url")!.GetValue(data)!;
            return url[(url.IndexOf("#t=", StringComparison.Ordinal) + 3)..];
        }

        public Task<SparkMailReceipt> SendAsync(SparkMailRequest request, CancellationToken cancellationToken = default)
        {
            Sent.Add(request);
            return Task.FromResult<SparkMailReceipt>(null!);
        }

        public Task SendCampaignAsync(SparkMailCampaign campaign, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
