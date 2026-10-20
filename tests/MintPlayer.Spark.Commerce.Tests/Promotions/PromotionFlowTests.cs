using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.Claims;
using MintPlayer.Spark.Commerce.Checkout;
using MintPlayer.Spark.Commerce.Inventory;
using MintPlayer.Spark.Commerce.Pricing;
using MintPlayer.Spark.Commerce.Promotions;
using MintPlayer.Spark.Commerce.Promotions.Models;
using MintPlayer.Spark.Commerce.Promotions.Services;
using MintPlayer.Spark.Commerce.Tests.Checkout;
using MintPlayer.Spark.Commerce.Tests.Payments;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Commerce.Tests.Promotions;

/// <summary>
/// Promotions against RavenDB and the real inventory engine (M8): capacity blocks, exact usage limits, the waiting list's
/// activation and offers, back-in-stock batches, the code and sale-price pricing steps.
/// </summary>
public class PromotionFlowTests : SparkTestDriver
{
    private static readonly DateTimeOffset Start = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);

    private readonly CheckoutClock clock = new(Start);
    private readonly SparkCommercePromotionsOptions options = new();
    private readonly TestCapacitySource source = new();
    private InventoryEngine engine = null!;
    private ServiceProvider provider = null!;

    private const string ItemId = "items/concert";
    private const string QuotaId = "quotas/concert";

    private async Task<IServiceScope> SetUpAsync(int capacity)
    {
        engine = new InventoryEngine(Store, options: new InventoryEngineOptions { Grace = TimeSpan.FromSeconds(1) }, clock: clock);
        await engine.CreateQuotaAsync(QuotaId, capacity, 1);
        await engine.MapItemAsync(ItemId, [new ItemQuota(QuotaId)]);
        source.Items[ItemId] = new CheckoutItem
        {
            ItemId = ItemId, Channel = "tickets", Price = new Money(40m, "EUR"), Title = TranslatedString.Of("en", "Concert"),
            Capacity = new CheckoutCapacity { Items = [new ItemLine(ItemId, 1)] },
        };

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IDocumentStore>(Store);
        services.AddSingleton<TimeProvider>(clock);
        services.AddSingleton<ISparkInventory>(engine);
        services.AddSingleton<ICheckoutCapacitySource>(source);
        services.AddSingleton<ISparkClaims>(new SparkClaims(Store, clock));
        services.AddSingleton<IOptionsMonitor<SparkCommercePromotionsOptions>>(new StaticOptionsMonitor<SparkCommercePromotionsOptions>(options));
        services.AddScoped<PromotionItems>();
        services.AddScoped<PromotionUsage>();
        services.AddScoped<PromotionBlocks>();
        services.AddScoped<PromotionMails>();
        services.AddScoped<WaitingLists>();
        services.AddScoped<BackInStock>();
        provider = services.BuildServiceProvider();
        return provider.CreateScope();
    }

    private async Task SellAsync(string claimId, int n)
    {
        (await engine.TryClaimAsync(new ClaimRequest(claimId, "orders/" + claimId, clock.GetUtcNow().AddMinutes(10), [], Items: [new ItemLine(ItemId, n)]))).Succeeded.Should().BeTrue();
        (await engine.ConvertAsync(claimId)).Succeeded.Should().BeTrue();
    }

    private async Task<int> PublicAvailableAsync() => (await engine.GetAvailabilityAsync(QuotaId)).Available;

    [Fact]
    public async Task A_block_takes_units_out_of_public_sale_and_closing_returns_only_the_unsold()
    {
        using var scope = await SetUpAsync(10);
        var blocks = scope.ServiceProvider.GetRequiredService<PromotionBlocks>();

        var block = await blocks.CreateAsync("PromotionCodes/PARTNER", "orgs/a", ItemId, 4, Start.AddDays(1), CancellationToken.None);
        block.Capacity.Should().Be(4);
        (await PublicAvailableAsync()).Should().Be(6);

        // A partner buys one unit from the block's own quota.
        (await engine.TryClaimAsync(new ClaimRequest("claims/partner-1", "orders/p1", Start.AddMinutes(10), [new QuotaLine(block.QuotaId, 1)]))).Succeeded.Should().BeTrue();
        (await engine.ConvertAsync("claims/partner-1")).Succeeded.Should().BeTrue();

        (await blocks.CloseAsync(block.Id!, CancellationToken.None)).Should().Be(3);
        (await PublicAvailableAsync()).Should().Be(9);
        (await blocks.CloseAsync(block.Id!, CancellationToken.None)).Should().Be(0); // idempotent (I4)

        // A refund after the close comes back to the block, and the reconciliation hands it to the public.
        (await engine.ReturnAsync("claims/partner-1", new InventoryReturn("refunds/p1", ReturnReason.Refund))).Succeeded.Should().BeTrue();
        (await blocks.ReturnSpareAsync(block.Id!, int.MaxValue, CancellationToken.None)).Should().Be(1);
        (await PublicAvailableAsync()).Should().Be(10);
    }

    [Fact]
    public async Task A_block_cannot_take_more_than_the_public_has_left()
    {
        using var scope = await SetUpAsync(3);
        var blocks = scope.ServiceProvider.GetRequiredService<PromotionBlocks>();
        var block = await blocks.CreateAsync("PromotionCodes/BIG", null, ItemId, 5, null, CancellationToken.None);
        block.Capacity.Should().Be(0); // all or nothing (I2): the claim of 5 was refused
        block.Pending.Should().BeNull();
        (await PublicAvailableAsync()).Should().Be(3);
    }

    [Fact]
    public async Task A_usage_limit_is_exact_idempotent_by_owner_and_lapses_with_the_reservation()
    {
        using var scope = await SetUpAsync(1);
        var usage = scope.ServiceProvider.GetRequiredService<PromotionUsage>();
        var code = new PromotionCode { Code = "TWICE", MaxUses = 2 };
        var until = Start.AddMinutes(30);

        (await usage.ReserveAsync(code, "lines:a", null, until, CancellationToken.None)).Should().Be(UsageResult.Reserved);
        (await usage.ReserveAsync(code, "lines:b", null, until, CancellationToken.None)).Should().Be(UsageResult.Reserved);
        (await usage.ReserveAsync(code, "lines:c", null, until, CancellationToken.None)).Should().Be(UsageResult.Exhausted);
        (await usage.ReserveAsync(code, "lines:a", null, until, CancellationToken.None)).Should().Be(UsageResult.Reserved); // a retried placement
        (await usage.CountAsync(code, CancellationToken.None)).Should().Be(2);

        await usage.SetDeadlineAsync(code, "lines:a", null, null, CancellationToken.None); // confirmed: permanent
        await usage.ReleaseAsync(code, "lines:b", null, CancellationToken.None);             // ended unpaid
        (await usage.ReserveAsync(code, "lines:c", null, until, CancellationToken.None)).Should().Be(UsageResult.Reserved);

        clock.Advance(TimeSpan.FromHours(1)); // c's reservation lapsed, a's is permanent
        (await usage.CountAsync(code, CancellationToken.None)).Should().Be(1);
        (await usage.HasRoomAsync(code, "lines:d", CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task Once_per_buyer_refuses_a_second_order_of_the_same_buyer()
    {
        using var scope = await SetUpAsync(1);
        var usage = scope.ServiceProvider.GetRequiredService<PromotionUsage>();
        var code = new PromotionCode { Code = "WELCOME", OncePerBuyer = true };
        var until = Start.AddMinutes(30);

        (await usage.ReserveAsync(code, "lines:a", "user:users/1", until, CancellationToken.None)).Should().Be(UsageResult.Reserved);
        (await usage.ReserveAsync(code, "lines:b", "user:users/1", until, CancellationToken.None)).Should().Be(UsageResult.AlreadyUsedByBuyer);
        (await usage.ReserveAsync(code, "lines:b", "user:users/2", until, CancellationToken.None)).Should().Be(UsageResult.Reserved);
        await usage.ReleaseAsync(code, "lines:a", "user:users/1", CancellationToken.None);
        (await usage.ReserveAsync(code, "lines:c", "user:users/1", until, CancellationToken.None)).Should().Be(UsageResult.Reserved);
    }

    [Fact]
    public async Task The_code_step_reserves_the_last_use_at_placement_and_refuses_another_placement()
    {
        using var scope = await SetUpAsync(1);
        var usage = scope.ServiceProvider.GetRequiredService<PromotionUsage>();
        using (var session = Store.OpenAsyncSession())
        {
            await session.StoreAsync(new PromotionCode
            {
                Id = PromotionCode.IdFor("LAST1"), Code = "LAST1", Effect = PromotionEffect.FixedAmountOff, Amount = new Money(5m, "EUR"), MaxUses = 1,
            });
            await session.SaveChangesAsync();
        }
        var step = new PromotionCodePricingStep(Store, usage, clock, new StaticOptionsMonitor<SparkCommercePromotionsOptions>(options));

        var first = Context("line-a", placing: true);
        await step.ApplyAsync(first, CancellationToken.None);
        first.Lines[0].Adjustments.Single().Amount.Should().Be(new Money(-5m, "EUR"));
        await step.ApplyAsync(Context("line-a", placing: true), CancellationToken.None); // the same placement again: idempotent

        var second = () => step.ApplyAsync(Context("line-b", placing: true), CancellationToken.None);
        (await second.Should().ThrowAsync<CheckoutException>()).Which.Code.Should().Be(PromotionRefusals.CodeExhausted);
        var quote = () => step.ApplyAsync(Context("line-b", placing: false), CancellationToken.None);
        (await quote.Should().ThrowAsync<CheckoutException>()).Which.Code.Should().Be(PromotionRefusals.CodeExhausted);
    }

    [Fact]
    public async Task The_sale_step_adjusts_a_line_to_the_sale_price_while_the_sale_runs()
    {
        using var scope = await SetUpAsync(1);
        using (var session = Store.OpenAsyncSession())
        {
            await session.StoreAsync(new PromotionSalePrice
            {
                Id = PromotionSalePrice.IdFor(ItemId, Start.AddHours(-1)), ItemId = ItemId, Channel = "tickets", SalePrice = new Money(30m, "EUR"),
                RegularPrice = new Money(40m, "EUR"), ValidFrom = Start.AddHours(-1), ValidUntil = Start.AddHours(1), SeriesStart = Start.AddHours(-1),
            });
            await session.SaveChangesAsync();
        }
        var commerce = new SparkCommerceOptions();
        commerce.Channels["tickets"] = new CommerceChannelOptions();
        var step = new SalePricePricingStep(Store, new FinalPriceCalculator(new StaticOptionsMonitor<SparkCommerceOptions>(commerce)), clock,
            new StaticOptionsMonitor<SparkCommercePromotionsOptions>(options));

        var context = Context("line-a", placing: false, quantity: 2);
        await step.ApplyAsync(context, CancellationToken.None);
        context.Lines[0].Adjustments.Single().Amount.Should().Be(new Money(-20m, "EUR"));
        context.Total.Should().Be(new Money(60m, "EUR"));

        clock.Advance(TimeSpan.FromHours(2)); // the sale ended
        var later = Context("line-a", placing: false, quantity: 2);
        await step.ApplyAsync(later, CancellationToken.None);
        later.Lines[0].Adjustments.Should().BeEmpty();
    }

    [Fact]
    public async Task The_waiting_list_activates_after_the_sell_out_and_offers_freed_units_strictly_in_order()
    {
        options.WaitingList.OfferValidity = TimeSpan.FromHours(24);
        using var scope = await SetUpAsync(2);
        var lists = scope.ServiceProvider.GetRequiredService<WaitingLists>();
        var list = await lists.CreateAsync(ItemId, null, null, null, null, CancellationToken.None);
        await lists.JoinAsync(list.Id!, "users/1", 1, CancellationToken.None);
        await lists.JoinAsync(list.Id!, "users/2", 2, CancellationToken.None);
        await lists.JoinAsync(list.Id!, "users/3", 1, CancellationToken.None);

        await lists.EvaluateAsync(list.Id!, CancellationToken.None);
        (await LoadAsync<PromotionWaitingList>(list.Id!))!.Status.Should().Be(PromotionWaitingListStatus.Open); // not sold out yet

        await SellAsync("claims/s1", 2);
        await lists.EvaluateAsync(list.Id!, CancellationToken.None);
        var active = (await LoadAsync<PromotionWaitingList>(list.Id!))!;
        active.Status.Should().Be(PromotionWaitingListStatus.Active);

        // A refund frees one unit: it goes to the list, not back to the public, and to the first entry.
        (await engine.ReturnAsync("claims/s1", new InventoryReturn("refunds/1", ReturnReason.Refund, Items: [new ItemLine(ItemId, 1)]))).Succeeded.Should().BeTrue();
        await lists.EvaluateAsync(list.Id!, CancellationToken.None);
        (await PublicAvailableAsync()).Should().Be(0);
        var first = (await LoadAsync<PromotionWaitingListEntry>(PromotionWaitingListEntry.IdFor(list.Id!, "users/1")))!;
        first.Status.Should().Be(PromotionWaitingListEntryStatus.Offered);
        var offer = (await LoadAsync<PromotionCode>(PromotionCode.IdFor(first.OfferCode!)))!;
        offer.AssignedToUserId.Should().Be("users/1");
        offer.MaxQuantityPerOrder.Should().Be(1);
        offer.BlockId.Should().Be(active.BlockId);

        // The offer lapses: its unit goes to the next entry, who wants two, so it waits (strict order) and so does users/3.
        clock.Advance(TimeSpan.FromHours(25));
        await lists.EvaluateAsync(list.Id!, CancellationToken.None);
        (await LoadAsync<PromotionWaitingListEntry>(first.Id!))!.Status.Should().Be(PromotionWaitingListEntryStatus.OfferExpired);
        (await LoadAsync<PromotionCode>(offer.Id!))!.Active.Should().BeFalse();
        (await LoadAsync<PromotionWaitingListEntry>(PromotionWaitingListEntry.IdFor(list.Id!, "users/2")))!.Status.Should().Be(PromotionWaitingListEntryStatus.Waiting);

        (await engine.ReturnAsync("claims/s1", new InventoryReturn("refunds/2", ReturnReason.Refund, Items: [new ItemLine(ItemId, 1)]))).Succeeded.Should().BeTrue();
        await lists.EvaluateAsync(list.Id!, CancellationToken.None);
        (await LoadAsync<PromotionWaitingListEntry>(PromotionWaitingListEntry.IdFor(list.Id!, "users/2")))!.Status.Should().Be(PromotionWaitingListEntryStatus.Offered);

        // Closing the list lapses the offer and returns the spare units to the public.
        await lists.CloseAsync(list.Id!, CancellationToken.None);
        (await PublicAvailableAsync()).Should().Be(2);
    }

    [Fact]
    public async Task The_waiting_list_waits_for_holds_to_settle_before_it_activates()
    {
        using var scope = await SetUpAsync(1);
        var lists = scope.ServiceProvider.GetRequiredService<WaitingLists>();
        var list = await lists.CreateAsync(ItemId, null, null, null, null, CancellationToken.None);
        (await engine.TryClaimAsync(new ClaimRequest("claims/held", "baskets/1", Start.AddMinutes(10), [], Items: [new ItemLine(ItemId, 1)]))).Succeeded.Should().BeTrue();

        await lists.EvaluateAsync(list.Id!, CancellationToken.None);
        (await LoadAsync<PromotionWaitingList>(list.Id!))!.Status.Should().Be(PromotionWaitingListStatus.Open); // sold out, but a hold may lapse back
    }

    [Fact]
    public async Task A_restock_mails_the_oldest_subscribers_in_a_batch_of_restocked_times_factor()
    {
        options.BackInStock.BatchFactor = 2m;
        using var scope = await SetUpAsync(1);
        using (var session = Store.OpenAsyncSession())
        {
            for (var i = 1; i <= 3; i++)
                await session.StoreAsync(new SparkUser { Id = $"users/{i}", UserName = $"buyer{i}", Email = $"buyer{i}@example.org", EmailConfirmed = true });
            await session.SaveChangesAsync();
        }
        var backInStock = scope.ServiceProvider.GetRequiredService<BackInStock>();
        await SellAsync("claims/s1", 1);
        for (var i = 1; i <= 3; i++)
        {
            await backInStock.SubscribeAsync(ItemId, $"users/{i}", null, null, CancellationToken.None);
            clock.Advance(TimeSpan.FromSeconds(1));
        }
        var (guest, sent) = await backInStock.SubscribeAsync(ItemId, null, "guest@example.org", null, CancellationToken.None);
        guest.Confirmed.Should().BeFalse();
        sent.Should().BeTrue(); // double opt-in

        await backInStock.RunAsync(CancellationToken.None); // sold out: nothing
        (await NotifiedAsync()).Should().BeEmpty();

        (await engine.ReturnAsync("claims/s1", new InventoryReturn("refunds/1", ReturnReason.Refund))).Succeeded.Should().BeTrue();
        await backInStock.RunAsync(CancellationToken.None);
        (await NotifiedAsync()).Should().BeEquivalentTo(["buyer1@example.org", "buyer2@example.org"]); // 1 unit × 2, oldest first; never the unconfirmed guest

        await backInStock.RunAsync(CancellationToken.None); // still in stock: no second batch without a new restock
        (await NotifiedAsync()).Should().HaveCount(2);

        await backInStock.EraseAsync("users/3", "buyer3@example.org", CancellationToken.None);
        (await LoadAsync<PromotionStockSubscription>(PromotionStockSubscription.IdFor(ItemId, "buyer3@example.org"))).Should().BeNull();
    }

    private async Task<List<string>> NotifiedAsync()
    {
        using var session = Store.OpenAsyncSession();
        return (await session.Query<PromotionStockSubscription>().Customize(c => c.WaitForNonStaleResults()).Where(s => s.NotifiedAt != null).ToListAsync())
            .Select(s => s.Email).ToList();
    }

    private async Task<T?> LoadAsync<T>(string id)
    {
        using var session = Store.OpenAsyncSession();
        return await session.LoadAsync<T>(id);
    }

    private CheckoutPricingContext Context(string lineId, bool placing, int quantity = 1)
    {
        var item = source.Items[ItemId];
        var gross = item.Price * quantity;
        return new CheckoutPricingContext
        {
            Currency = "EUR",
            Lines = [new PricedLine
            {
                LineId = lineId, Item = item, Quantity = quantity,
                Price = new FinalPrice(gross, Money.Zero("EUR"), gross, [], [], gross, item.Price),
            }],
            Choices = new Dictionary<string, string> { [PromotionChoices.Code] = "last1" },
            Placing = placing,
        };
    }
}
