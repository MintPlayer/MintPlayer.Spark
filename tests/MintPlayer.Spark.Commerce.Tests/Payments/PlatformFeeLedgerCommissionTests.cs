using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Commerce.Checkout.Models;
using MintPlayer.Spark.Commerce.Payments;
using MintPlayer.Spark.Commerce.Payments.Models;
using MintPlayer.Spark.Commerce.Payments.Services;
using MintPlayer.Spark.Commerce.Promotions;
using MintPlayer.Spark.Commerce.Promotions.Models;
using MintPlayer.Spark.Commerce.Promotions.Services;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Commerce.Tests.Payments;

/// <summary>
/// The platform-fee ledger's commission kind and public booking API (GB4/GC2, item 5 of the M4–M16 follow-ups), and
/// Promotions' tracking-link commissions booked through it instead of their former own ledger.
/// </summary>
public class PlatformFeeLedgerCommissionTests : SparkTestDriver
{
    private const string Booker = "commerce-promotions";
    private const string Seller = "Organizations/seller-1";
    private readonly ManualTimeProvider clock = new(DateTimeOffset.Parse("2026-10-19T10:00:00Z"));

    private ISparkPlatformFeeLedger Ledger() => new PlatformFeeLedger(clock);

    private static CommissionBooking Booking(string orderId, decimal amount, string reference = "PROMO1") => new()
    {
        Booker = Booker,
        SourceId = orderId,
        Amount = new Money(amount, "EUR"),
        SellerOrganizationId = Seller,
        Reference = orderId,
        BeneficiaryId = "users/promoter",
        AttributionRef = reference,
        OccurredAt = DateTimeOffset.Parse("2026-10-19T10:00:00Z"),
    };

    [Fact]
    public async Task A_commission_is_booked_once_under_its_natural_id()
    {
        var ledger = Ledger();
        using (var session = Store.OpenAsyncSession())
        {
            (await ledger.BookCommissionAsync(session, Booking("orders/1", 5m))).Should().NotBeNull();
            await session.SaveChangesAsync();
        }
        using (var session = Store.OpenAsyncSession())
        {
            (await ledger.BookCommissionAsync(session, Booking("orders/1", 7m))).Should().BeNull();
            await session.SaveChangesAsync();
        }

        using var read = Store.OpenAsyncSession();
        var entry = await read.LoadAsync<PlatformFeeEntry>("PlatformFeeEntries/Commission/commerce-promotions/orders/1");
        entry.Should().NotBeNull();
        entry!.Kind.Should().Be(PlatformFeeEntryKind.Commission);
        entry.Amount.Should().Be(new Money(5m, "EUR"));
        entry.SellerOrganizationId.Should().Be(Seller);
        entry.BeneficiaryId.Should().Be("users/promoter");
        entry.AttributionRef.Should().Be("PROMO1");
        entry.SignedCommissionMinorUnits.Should().Be(500);
        // Not platform revenue: the monthly fee invoice (Invoicing sums SignedMinorUnits) ignores it.
        entry.SignedMinorUnits.Should().Be(0);
    }

    [Fact]
    public async Task A_zero_commission_books_nothing_and_a_negative_one_is_refused()
    {
        var ledger = Ledger();
        using var session = Store.OpenAsyncSession();
        (await ledger.BookCommissionAsync(session, Booking("orders/2", 0m))).Should().BeNull();
        var negative = async () => await ledger.BookCommissionAsync(session, Booking("orders/2", -1m));
        await negative.Should().ThrowAsync<ArgumentException>();
        var noSource = async () => await ledger.BookCommissionAsync(session, Booking("", 1m));
        await noSource.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Reversals_book_only_the_missing_part_once_per_key_and_never_more_than_the_commission()
    {
        var ledger = Ledger();
        using (var session = Store.OpenAsyncSession())
        {
            await ledger.BookCommissionAsync(session, Booking("orders/3", 10m));
            await session.SaveChangesAsync();
        }

        async Task<Money> Reverse(string key, decimal total)
        {
            using var session = Store.OpenAsyncSession();
            var delta = await ledger.ReverseCommissionAsync(session, Booker, "orders/3", key, new Money(total, "EUR"), clock.GetUtcNow());
            await session.SaveChangesAsync();
            return delta;
        }

        (await Reverse("refund-1", 4m)).Should().Be(new Money(4m, "EUR"));
        (await Reverse("refund-1", 4m)).Should().Be(Money.Zero("EUR"));   // redelivered
        (await Reverse("refund-2", 3m)).Should().Be(Money.Zero("EUR"));   // the running total is already past 3
        (await Reverse("refund-3", 7m)).Should().Be(new Money(3m, "EUR"));
        (await Reverse("dispute-1", 25m)).Should().Be(new Money(3m, "EUR")); // capped at the commission's 10

        using var read = Store.OpenAsyncSession();
        var reversals = await read.Advanced.LoadStartingWithAsync<PlatformFeeEntry>("PlatformFeeEntries/CommissionReversal/commerce-promotions/orders/3/reversal/");
        reversals.Should().HaveCount(3);
        reversals.Sum(r => r.Amount.ToMinorUnits()).Should().Be(1000);
        reversals.Should().OnlyContain(r => r.Kind == PlatformFeeEntryKind.CommissionReversal && r.AttributionRef == "PROMO1" && r.SignedMinorUnits == 0);
    }

    [Fact]
    public async Task Reversing_an_unbooked_commission_books_nothing()
    {
        using var session = Store.OpenAsyncSession();
        var delta = await Ledger().ReverseCommissionAsync(session, Booker, "orders/none", "refund-1", new Money(5m, "EUR"), clock.GetUtcNow());
        delta.IsZero.Should().BeTrue();
        await session.SaveChangesAsync();
        (await session.Advanced.LoadStartingWithAsync<PlatformFeeEntry>("PlatformFeeEntries/")).Should().BeEmpty();
    }

    [Fact]
    public async Task Commissions_are_listed_by_attribution_with_their_reversals()
    {
        var ledger = Ledger();
        using (var session = Store.OpenAsyncSession())
        {
            await ledger.BookCommissionAsync(session, Booking("orders/4", 6m, "LINK-A"));
            await ledger.BookCommissionAsync(session, Booking("orders/5", 2m, "LINK-B"));
            await session.SaveChangesAsync();
        }
        using (var session = Store.OpenAsyncSession())
        {
            await ledger.ReverseCommissionAsync(session, Booker, "orders/4", "refund-1", new Money(1m, "EUR"), clock.GetUtcNow());
            await session.SaveChangesAsync();
        }
        WaitForIndexing(Store);

        using var read = Store.OpenAsyncSession();
        var listed = await ledger.ListCommissionsAsync(read, "LINK-A");
        listed.Should().HaveCount(2);
        listed.Sum(e => e.SignedCommissionMinorUnits).Should().Be(500);
    }

    [Fact]
    public async Task Tracking_books_the_link_commission_into_the_ledger_and_reverses_it_on_a_partial_refund()
    {
        await using var provider = TrackingServices();
        using (var session = Store.OpenAsyncSession())
        {
            await session.StoreAsync(new PromotionTrackingLink
            {
                Id = PromotionTrackingLink.IdFor("PROMO1"), Ref = "PROMO1", OrganizationId = Seller, Name = "Promoter",
                PromoterUserId = "users/promoter", CommissionBasisPoints = 1000,
            });
            await session.StoreAsync(new PromotionOrderAttribution
            {
                Id = PromotionOrderAttribution.IdFor("orders/10"), OrderId = "orders/10", Ref = "PROMO1", OrganizationId = Seller,
                Status = PromotionOrderAttributionStatus.Placed,
            });
            await session.SaveChangesAsync();
        }
        var order = Order("orders/10", 100m);

        using (var scope = provider.CreateScope())
        {
            var tracking = scope.ServiceProvider.GetRequiredService<Tracking>();
            await tracking.OnConfirmedAsync(order, CancellationToken.None);
            await tracking.OnConfirmedAsync(order, CancellationToken.None); // redelivered: booked once
            order.AmountRefunded = new Money(25m, "EUR");
            await tracking.OnReversedAsync(order, "refund-1", full: false, CancellationToken.None);
            await tracking.OnReversedAsync(order, "refund-1", full: false, CancellationToken.None);
        }

        using var read = Store.OpenAsyncSession();
        var commission = await read.LoadAsync<PlatformFeeEntry>(PlatformFeeEntry.IdFor(PlatformFeeEntryKind.Commission, Tracking.LedgerBooker, "orders/10"));
        commission!.Amount.Should().Be(new Money(10m, "EUR"));
        commission.BeneficiaryId.Should().Be("users/promoter");
        var reversals = await read.Advanced.LoadStartingWithAsync<PlatformFeeEntry>("PlatformFeeEntries/CommissionReversal/");
        reversals.Should().ContainSingle().Which.Amount.Should().Be(new Money(2.5m, "EUR"));
        provider.GetRequiredService<RecordingAudit>().Actions.Should().Equal(PromotionAuditActions.CommissionBooked, PromotionAuditActions.CommissionReversed);

        WaitForIndexing(Store);
        using var scope2 = provider.CreateScope();
        var report = await scope2.ServiceProvider.GetRequiredService<Tracking>().ReportAsync("PROMO1", CancellationToken.None);
        report.Commissions.Should().ContainSingle().Which.Should().Be(new Money(7.5m, "EUR"));
    }

    [Fact]
    public void The_reversal_target_is_the_refunded_share_or_everything_on_a_full_return()
    {
        var order = Order("orders/11", 80m);
        order.AmountRefunded = new Money(20m, "EUR");
        Tracking.ReversalTarget(new Money(8m, "EUR"), order, full: false).Should().Be(new Money(2m, "EUR"));
        Tracking.ReversalTarget(new Money(8m, "EUR"), order, full: true).Should().Be(new Money(8m, "EUR"));
        order.AmountRefunded = new Money(500m, "EUR");
        Tracking.ReversalTarget(new Money(8m, "EUR"), order, full: false).Should().Be(new Money(8m, "EUR"));
    }

    private static CheckoutOrder Order(string id, decimal total) => new()
    {
        Id = id,
        Currency = "EUR",
        Total = new Money(total, "EUR"),
        Lines =
        [
            new CheckoutOrderLine
            {
                LineId = "l1", ItemId = "items/1", SellerOrganizationId = Seller, Quantity = 1, Status = OrderLineStatus.Active,
                UnitPrice = new Money(total, "EUR"), Total = new Money(total, "EUR"),
            },
        ],
    };

    private ServiceProvider TrackingServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IDocumentStore>(Store);
        services.AddSingleton<TimeProvider>(clock);
        services.AddSingleton<RecordingAudit>();
        services.AddSingleton<ICommerceAudit>(sp => sp.GetRequiredService<RecordingAudit>());
        services.AddScoped<PlatformFeeLedger>();
        services.AddScoped<ISparkPlatformFeeLedger>(sp => sp.GetRequiredService<PlatformFeeLedger>());
        services.AddSingleton<Microsoft.Extensions.Options.IOptionsMonitor<SparkCommercePromotionsOptions>>(
            new StaticOptionsMonitor<SparkCommercePromotionsOptions>(new SparkCommercePromotionsOptions()));
        services.AddScoped<Tracking>();
        return services.BuildServiceProvider();
    }

    private sealed class RecordingAudit : ICommerceAudit
    {
        public List<string> Actions { get; } = [];

        public Task RecordAsync(IAsyncDocumentSession session, CommerceAuditEntry entry, CancellationToken cancellationToken = default)
        {
            Actions.Add(entry.Action);
            return Task.CompletedTask;
        }
    }
}
