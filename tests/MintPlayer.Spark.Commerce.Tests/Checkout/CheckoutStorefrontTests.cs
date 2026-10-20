using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.Commerce.Checkout;
using MintPlayer.Spark.Commerce.Checkout.Models;
using MintPlayer.Spark.Commerce.Checkout.Services;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents;
using CheckoutContracts = MintPlayer.Spark.Commerce.Checkout.Contracts;

namespace MintPlayer.Spark.Commerce.Tests.Checkout;

/// <summary>
/// M17: the hold timer's "More time" (GA10, WCAG 2.2.1 "Extend", capped at <c>Hold:Max</c>) and the account area's order
/// side (GB10): the history, and attaching guest orders with the account's <b>confirmed</b> e-mail address (D4).
/// </summary>
public class CheckoutStorefrontTests : SparkTestDriver
{
    private CheckoutHost Host() => CheckoutHost.Build(Store, configure: (services, _, _) => services.AddScoped<CheckoutAccount>());

    private static async Task<CheckoutException> Refused(Func<Task> action)
    {
        try { await action(); }
        catch (CheckoutException ex) { return ex; }
        throw new InvalidOperationException("Expected a checkout refusal.");
    }

    // ---- "More time" ---------------------------------------------------------------------------------------------

    [Fact]
    public void More_time_adds_the_extension_and_stops_at_the_max()
    {
        var hold = new HoldBounds { Default = TimeSpan.FromMinutes(10), Min = TimeSpan.FromMinutes(5), Max = TimeSpan.FromMinutes(20) };
        var since = DateTimeOffset.Parse("2026-10-20T10:00:00Z");
        hold.Extend(since, since.AddMinutes(10)).Should().Be(since.AddMinutes(15));
        hold.Extend(since, since.AddMinutes(17)).Should().Be(since.AddMinutes(20));
        hold.Extend(since, since.AddMinutes(20)).HasValue.Should().BeFalse();
        new HoldBounds { Max = TimeSpan.FromMinutes(20), Extension = TimeSpan.Zero }.Extend(since, since.AddMinutes(10)).HasValue.Should().BeFalse();
    }

    [Fact]
    public async Task More_time_moves_the_hold_and_the_claim_until_Hold_Max_then_refuses()
    {
        var host = Host();
        await host.ItemAsync("tickets/a", "tickets", 25m);
        var start = host.Clock.Now;

        var basket = await host.Checkout.AddLineAsync(new CheckoutContracts.SparkBasketAddRequest("tickets/a", 2));
        basket.EarliestHoldUntil.Should().Be(start.AddMinutes(10));
        basket.ExtendableUntil.Should().Be(start.AddMinutes(15));

        host.Clock.Advance(TimeSpan.FromMinutes(8));
        basket = await host.Checkout.ExtendHoldAsync();
        basket.EarliestHoldUntil.Should().Be(start.AddMinutes(15));
        basket.ExtendableUntil.Should().Be(start.AddMinutes(20));

        basket = await host.Checkout.ExtendHoldAsync();
        basket.EarliestHoldUntil.Should().Be(start.AddMinutes(20));
        basket.ExtendableUntil.HasValue.Should().BeFalse();

        (await Refused(() => host.Checkout.ExtendHoldAsync())).Code.Should().Be(CheckoutRefusals.HoldNotExtendable);

        // The claim moved with the line: past the original 10 minutes the seats are still held.
        host.Clock.Advance(TimeSpan.FromMinutes(5));
        (await host.AvailableAsync("tickets/a")).Should().Be(8);
    }

    [Fact]
    public async Task A_quantity_change_keeps_the_hold_deadline_so_Hold_Max_bounds_the_total()
    {
        // Red before the fix: every quantity change restarted the hold (Now + Hold) and its HeldSince, so changing the
        // quantity again and again kept seats forever.
        var host = Host();
        await host.ItemAsync("tickets/a", "tickets", 25m);
        var start = host.Clock.Now;
        var basket = await host.Checkout.AddLineAsync(new CheckoutContracts.SparkBasketAddRequest("tickets/a", 2));
        var lineId = basket.Lines.Single().LineId;

        host.Clock.Advance(TimeSpan.FromMinutes(8));
        basket = await host.Checkout.SetQuantityAsync(lineId, 3); // an increase claims the extra unit under the same deadline
        basket.EarliestHoldUntil.Should().Be(start.AddMinutes(10));
        (await host.AvailableAsync("tickets/a")).Should().Be(7);

        basket = await host.Checkout.SetQuantityAsync(lineId, 1); // a decrease keeps the hold too
        basket.EarliestHoldUntil.Should().Be(start.AddMinutes(10));

        // "More time" still stops at Hold:Max counted from the first hold.
        await host.Checkout.ExtendHoldAsync();
        basket = await host.Checkout.ExtendHoldAsync();
        basket.EarliestHoldUntil.Should().Be(start.AddMinutes(20));
        host.Clock.Advance(TimeSpan.FromMinutes(5));
        basket = await host.Checkout.SetQuantityAsync(lineId, 2);
        basket.EarliestHoldUntil.Should().Be(start.AddMinutes(20));
        (await Refused(() => host.Checkout.ExtendHoldAsync())).Code.Should().Be(CheckoutRefusals.HoldNotExtendable);
    }

    [Fact]
    public void Only_a_lapsed_hold_starts_afresh_on_a_quantity_change()
    {
        var since = DateTimeOffset.Parse("2026-10-20T10:00:00Z");
        var line = new CheckoutBasketLine { AddedAt = since, HeldSince = since, HeldUntil = since.AddMinutes(10) };
        CheckoutService.HoldAfterQuantityChange(line, since.AddMinutes(8), TimeSpan.FromMinutes(10)).Should().Be((since.AddMinutes(10), since));
        CheckoutService.HoldAfterQuantityChange(line, since.AddMinutes(11), TimeSpan.FromMinutes(10)).Should().Be((since.AddMinutes(21), since.AddMinutes(11)));
    }

    [Fact]
    public async Task More_time_does_not_revive_a_lapsed_hold()
    {
        var host = Host();
        await host.ItemAsync("tickets/a", "tickets", 25m);
        await host.Checkout.AddLineAsync(new CheckoutContracts.SparkBasketAddRequest("tickets/a", 1));

        host.Clock.Advance(TimeSpan.FromMinutes(11));
        (await Refused(() => host.Checkout.ExtendHoldAsync())).Code.Should().Be(CheckoutRefusals.HoldNotExtendable);
    }

    // ---- account area ----------------------------------------------------------------------------------------------

    private async Task StoreUserAsync(string id, string email, bool confirmed)
    {
        using var session = Store.OpenAsyncSession();
        await session.StoreAsync(new SparkUser { Id = id, UserName = id, Email = email, EmailConfirmed = confirmed });
        await session.SaveChangesAsync();
    }

    [Fact]
    public async Task A_confirmed_address_attaches_the_guest_orders_and_raises_the_event_for_tickets()
    {
        var host = Host();
        await host.ItemAsync("shop/a", "shop", 10m);
        await host.Checkout.AddLineAsync(new CheckoutContracts.SparkBasketAddRequest("shop/a", 1));
        var placed = await host.PlaceAsync(email: "buyer@example.org");

        await StoreUserAsync("users/1", "Buyer@Example.org", confirmed: true);
        host.Buyer.UserId = "users/1";
        WaitForIndexing(Store);
        var account = host.Get<CheckoutAccount>();

        (await account.GuestOrdersAsync(default))!.Numbers.Should().Contain(placed.Number);
        var attached = await account.AttachGuestOrdersAsync(default);
        attached!.Numbers.Should().Equal(placed.Number);

        using (var session = Store.OpenAsyncSession())
        {
            var order = await session.Query<CheckoutOrder>().Customize(c => c.WaitForNonStaleResults()).Where(o => o.Number == placed.Number).SingleAsync();
            order.BuyerUserId.Should().Be("users/1");
        }
        host.Events.Events.Should().Contain(e => e.Kind == OrderEventKind.AttachedToAccount && e.Reference == "visitor-1");

        WaitForIndexing(Store);
        var history = await account.OrdersAsync(0, 20, default);
        history!.Orders.Select(o => o.Number).Should().Contain(placed.Number);

        // Attaching twice moves nothing more.
        WaitForIndexing(Store);
        (await account.AttachGuestOrdersAsync(default))!.Numbers.Should().BeEmpty();
    }

    [Fact]
    public async Task An_unconfirmed_address_attaches_nothing()
    {
        var host = Host();
        await host.ItemAsync("shop/a", "shop", 10m);
        await host.Checkout.AddLineAsync(new CheckoutContracts.SparkBasketAddRequest("shop/a", 1));
        await host.PlaceAsync(email: "buyer@example.org");

        await StoreUserAsync("users/2", "buyer@example.org", confirmed: false);
        host.Buyer.UserId = "users/2";
        var account = host.Get<CheckoutAccount>();

        var info = await account.GuestOrdersAsync(default);
        info!.EmailConfirmed.Should().BeFalse();
        info.Numbers.Should().BeEmpty();
        (await Refused(() => account.AttachGuestOrdersAsync(default))).Code.Should().Be(CheckoutRefusals.EmailNotConfirmed);
    }

    [Fact]
    public async Task Signed_out_the_account_area_answers_nothing()
    {
        var host = Host();
        host.Buyer.UserId = null;
        var account = host.Get<CheckoutAccount>();
        (await account.OrdersAsync(0, 20, default)).Should().BeNull();
        (await account.GuestOrdersAsync(default)).Should().BeNull();
        (await account.AttachGuestOrdersAsync(default)).Should().BeNull();
    }

    [Fact]
    public void Only_an_accountless_order_with_the_same_address_is_a_guest_order_of_it()
    {
        CheckoutAccount.IsGuestOrderOf(new CheckoutOrder { BuyerEmail = " Buyer@Example.org " }, "buyer@example.org").Should().BeTrue();
        CheckoutAccount.IsGuestOrderOf(new CheckoutOrder { BuyerEmail = "buyer@example.org", BuyerUserId = "users/9" }, "buyer@example.org").Should().BeFalse();
        CheckoutAccount.IsGuestOrderOf(new CheckoutOrder { BuyerEmail = "other@example.org" }, "buyer@example.org").Should().BeFalse();
        CheckoutAccount.IsGuestOrderOf(new CheckoutOrder(), "buyer@example.org").Should().BeFalse();
    }
}
