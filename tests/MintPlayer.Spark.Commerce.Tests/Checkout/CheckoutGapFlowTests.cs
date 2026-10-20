using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Commerce.Checkout;
using MintPlayer.Spark.Commerce.Checkout.Models;
using MintPlayer.Spark.Commerce.Checkout.Services;
using MintPlayer.Spark.Commerce.Tests.Payments;
using MintPlayer.Spark.Commerce.Inventory;
using MintPlayer.Spark.Commerce.Promotions.Services;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;
using CheckoutContracts = MintPlayer.Spark.Commerce.Checkout.Contracts;

namespace MintPlayer.Spark.Commerce.Tests.Checkout;

/// <summary>
/// Checkout gaps closed after M7–M16: the guest identity in the pricing context (once-per-buyer codes for guests), the
/// per-sale hold time, the soft availability check of OnPayment lines (D1) and the pre-payment check seam (GB3).
/// </summary>
public class CheckoutGapFlowTests : SparkTestDriver
{
    private sealed class CapturingStep : ICheckoutPricingStep
    {
        public List<(string? UserId, string? Email, string? Visitor, bool Placing)> Seen { get; } = [];

        public int Order => 50;

        public Task ApplyAsync(CheckoutPricingContext context, CancellationToken cancellationToken)
        {
            Seen.Add((context.BuyerUserId, context.BuyerEmail, context.VisitorId, context.Placing));
            return Task.CompletedTask;
        }
    }

    private sealed class RequireData(string key) : ICheckoutPrePaymentCheck
    {
        public Task CheckAsync(CheckoutPrePaymentContext context, CancellationToken cancellationToken)
        {
            if (!context.Data.TryGetValue(key, out var value))
                throw CheckoutException.Of("dataRequired", context.Order.Lines[0].LineId);
            context.StoreWithOrder(new PrePaymentNote { Value = value, OrderId = context.Order.Id! }, "notes/" + context.Order.Id!.Split('/')[1]);
            return Task.CompletedTask;
        }
    }

    private sealed class PrePaymentNote
    {
        public string Value { get; set; } = "";
        public string OrderId { get; set; } = "";
    }

    private static async Task<CheckoutException> Refused(Func<Task> action)
    {
        try { await action(); }
        catch (CheckoutException ex) { return ex; }
        throw new InvalidOperationException("Expected a checkout refusal.");
    }

    [Fact]
    public async Task A_guest_is_identified_to_pricing_steps_by_email_and_visitor()
    {
        var step = new CapturingStep();
        var host = CheckoutHost.Build(Store, configure: (services, _, _) => services.AddSingleton<ICheckoutPricingStep>(step));
        await host.ItemAsync("tickets/g", "tickets", 10m);
        await host.Checkout.AddLineAsync(new CheckoutContracts.SparkBasketAddRequest("tickets/g", 1));

        await host.PlaceAsync(email: "Guest@Example.org");

        var placing = step.Seen.Last(s => s.Placing);
        placing.UserId.Should().BeNull();
        placing.Email.Should().Be("Guest@Example.org");
        placing.Visitor.Should().Be("visitor-1");
    }

    [Fact]
    public void Once_per_buyer_keys_prefer_the_account_then_the_address_then_the_visitor()
    {
        PromotionUsage.BuyerKey("users/1", "a@b.c", "v1").Should().Be("user:users/1");
        PromotionUsage.BuyerKey(null, " A@B.c ", "v1").Should().Be("email:a@b.c");
        PromotionUsage.BuyerKey(null, null, "v1").Should().Be("visitor:v1");
        PromotionUsage.BuyerKey(null).Should().BeNull();
    }

    [Fact]
    public async Task A_sales_own_hold_time_is_used_within_the_channels_bounds()
    {
        var host = CheckoutHost.Build(Store);
        var item = await host.ItemAsync("tickets/h", "tickets", 10m);
        host.Catalog.Items["tickets/h"] = item with { HoldTime = TimeSpan.FromMinutes(7) };
        var basket = await host.Checkout.AddLineAsync(new CheckoutContracts.SparkBasketAddRequest("tickets/h", 1));
        basket.EarliestHoldUntil.Should().Be(host.Clock.Now + TimeSpan.FromMinutes(7));

        var capped = await host.ItemAsync("tickets/h2", "tickets", 10m);
        host.Catalog.Items["tickets/h2"] = capped with { HoldTime = TimeSpan.FromHours(1) };
        var other = CheckoutHost.Build(Store);
        other.Buyer.VisitorId = "visitor-2"; // a basket of its own
        other.Catalog.Items["tickets/h2"] = host.Catalog.Items["tickets/h2"];
        var second = await other.Checkout.AddLineAsync(new CheckoutContracts.SparkBasketAddRequest("tickets/h2", 1));
        second.EarliestHoldUntil.Should().Be(other.Clock.Now + host.Commerce.Channels["tickets"].Hold.Max);
    }

    [Fact]
    public async Task An_OnPayment_line_that_is_visibly_sold_out_is_refused_before_paying()
    {
        var host = CheckoutHost.Build(Store);
        await host.ItemAsync("shop/gone", "shop", 12m, capacity: 1);
        await host.Checkout.AddLineAsync(new CheckoutContracts.SparkBasketAddRequest("shop/gone", 1));
        (await host.Inventory.TryClaimAsync(new ClaimRequest("claims/elsewhere", "baskets/elsewhere", host.Clock.Now.AddMinutes(10), [],
            Items: [new ItemLine("shop/gone", 1)]))).Succeeded.Should().BeTrue();

        (await Refused(() => host.PlaceAsync())).Code.Should().Be(CheckoutRefusals.SoldOut);
        host.Payments.Created.Should().BeEmpty();
    }

    [Fact]
    public async Task A_pre_payment_check_refuses_pay_and_releases_the_claims_or_saves_its_documents_with_the_order()
    {
        var host = CheckoutHost.Build(Store, configure: (services, _, _) => services.AddSingleton<ICheckoutPrePaymentCheck>(new RequireData("answers")));
        await host.ItemAsync("box/q", "box", 15m, capacity: 2);
        await host.Checkout.AddLineAsync(new CheckoutContracts.SparkBasketAddRequest("box/q", 1));

        (await Refused(() => host.PlaceAsync())).Code.Should().Be("dataRequired");
        host.Payments.Created.Should().BeEmpty();
        (await host.AvailableAsync("box/q")).Should().Be(2); // the placement's claim was released

        var placed = await host.Checkout.PlaceOrderAsync(new CheckoutContracts.SparkPlaceOrderRequest(
            [host.Legal.TermsFor(null).Id], Email: "buyer@example.org", Name: "Test Buyer", Data: new() { ["answers"] = "42" }));

        using var session = Store.OpenAsyncSession();
        var order = await session.Query<CheckoutOrder>().Customize(c => c.WaitForNonStaleResults()).Where(o => o.Number == placed.Number).SingleAsync();
        var note = await session.LoadAsync<PrePaymentNote>("notes/" + order.Id!.Split('/')[1]);
        note.Should().NotBeNull();
        note!.Value.Should().Be("42");
    }

    /// <summary>Item 10 (D16 + X1): a line chosen as a guest keeps counting against the visitor's limit key after sign-in.</summary>
    [Fact]
    public async Task A_merged_guest_line_keeps_the_visitor_limit_key()
    {
        var host = CheckoutHost.Build(Store);
        var item = await host.ItemAsync("tickets/m", "tickets", 10m);
        host.Catalog.Items["tickets/m"] = item with { PerBuyerLimit = 4 };
        var visitor = host.Buyer.VisitorId!;
        await host.Checkout.AddLineAsync(new CheckoutContracts.SparkBasketAddRequest("tickets/m", 2));

        using (var session = Store.OpenAsyncSession(new SessionOptions { TransactionMode = TransactionMode.ClusterWide }))
        {
            await new CheckoutBasketMerge(new StaticOptionsMonitor<SparkCommerceOptions>(host.Commerce), host.Clock)
                .MergeAsync(visitor, "users/merged", session, default);
            await session.SaveChangesAsync();
        }
        host.Buyer.UserId = "users/merged";

        var placed = await host.PlaceAsync();

        using var read = Store.OpenAsyncSession();
        var order = await read.Query<CheckoutOrder>().Customize(c => c.WaitForNonStaleResults()).Where(o => o.Number == placed.Number).SingleAsync();
        var scope = OrderClaims.LimitScope(host.Catalog.Items["tickets/m"]);
        var visitorKey = InventoryLimitKeys.For(scope, 4, 2, new BuyerIdentifiers(VisitorId: visitor)).Single().Key;
        var accountKey = InventoryLimitKeys.For(scope, 4, 2, new BuyerIdentifiers(AccountId: "users/merged")).Single().Key;
        order.Lines.Single().Limits.Select(l => l.Key).Should().Contain(k => k == visitorKey);
        order.Lines.Single().Limits.Select(l => l.Key).Should().Contain(k => k == accountKey);
    }
}
