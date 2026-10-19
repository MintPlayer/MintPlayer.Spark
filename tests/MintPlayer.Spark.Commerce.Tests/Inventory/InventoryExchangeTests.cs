using MintPlayer.Spark.Commerce.Inventory;
using MintPlayer.Spark.Testing;

namespace MintPlayer.Spark.Commerce.Tests.Inventory;

/// <summary>
/// Item 2 (round 6): the exchange operation (return + claim + convert in one cluster-wide transaction), the commit
/// hook naming the units each commit touched, and the public read of an item's quota mapping.
/// </summary>
public class InventoryExchangeTests : SparkTestDriver
{
    private readonly TestClock clock = new(new DateTimeOffset(2026, 10, 19, 12, 0, 0, TimeSpan.Zero));
    private readonly List<InventoryCommit> commits = [];

    private InventoryEngine Engine() => new(Store, options: new InventoryEngineOptions { Grace = TimeSpan.FromSeconds(1) }, clock: clock,
        committed: (commit, _) =>
        {
            lock (commits) commits.Add(commit);
            return Task.CompletedTask;
        });

    private ClaimRequest Request(string id, IReadOnlyList<QuotaLine>? quotas = null, IReadOnlyList<string>? units = null,
        IReadOnlyList<LimitLine>? limits = null, IReadOnlyList<ChainLine>? chains = null)
        => new($"claims/{id}", "orders/1", clock.GetUtcNow().AddMinutes(10), quotas ?? [], units, limits, null, chains);

    private static async Task SoldAsync(InventoryEngine engine, ClaimRequest request)
    {
        (await engine.TryClaimAsync(request)).Succeeded.Should().BeTrue();
        (await engine.ConvertAsync(request.ClaimId)).Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task A_seat_change_gives_the_old_seat_back_and_sells_the_new_one_in_one_step()
    {
        var engine = Engine();
        await engine.CreateQuotaAsync("quotas/q", 10, 1);
        await SoldAsync(engine, Request("old", quotas: [new("quotas/q", 1)], units: ["S1"]));

        var result = await engine.ExchangeAsync(new InventoryExchange("claims/old", new InventoryReturn("change:1", ReturnReason.Other),
            Request("new", quotas: [new("quotas/q", 1)], units: ["S2"])));

        result.Succeeded.Should().BeTrue();
        result.Units.Should().BeEquivalentTo(new[] { "S2" });
        var states = await engine.GetUnitStatesAsync(["S1", "S2"]);
        states["S1"].Should().Be(UnitState.Free);
        states["S2"].Should().Be(UnitState.Sold);
        (await engine.GetAvailabilityAsync("quotas/q")).Should().Be(new QuotaAvailability("quotas/q", 10, 1, 0, 9));
        (await engine.GetClaimAsync("claims/old"))!.State.Should().Be(ClaimState.Released);
        (await engine.GetClaimAsync("claims/new"))!.State.Should().Be(ClaimState.Converted);
    }

    [Fact]
    public async Task A_refused_target_leaves_the_original_claim_untouched()
    {
        var engine = Engine();
        await engine.CreateQuotaAsync("quotas/q", 10, 1);
        await SoldAsync(engine, Request("old", quotas: [new("quotas/q", 1)], units: ["S1"]));
        await SoldAsync(engine, Request("other", quotas: [new("quotas/q", 1)], units: ["S2"]));

        var result = await engine.ExchangeAsync(new InventoryExchange("claims/old", new InventoryReturn("change:1", ReturnReason.Other),
            Request("new", quotas: [new("quotas/q", 1)], units: ["S2"])));

        result.Status.Should().Be(ClaimStatus.UnitTaken);
        result.Subject.Should().Be("S2");
        (await engine.GetUnitStatesAsync(["S1"]))["S1"].Should().Be(UnitState.Sold);
        var original = (await engine.GetClaimAsync("claims/old"))!;
        original.State.Should().Be(ClaimState.Converted);
        original.Returns.Should().BeEmpty();
        (await engine.GetClaimAsync("claims/new")).Should().BeNull();
        (await engine.GetAvailabilityAsync("quotas/q")).Sold.Should().Be(2);
    }

    [Fact]
    public async Task An_upgrade_on_the_same_seat_hands_the_seat_over_and_moves_the_quota()
    {
        var engine = Engine();
        await engine.CreateQuotaAsync("quotas/standard", 10, 1);
        await engine.CreateQuotaAsync("quotas/premium", 2, 1);
        await SoldAsync(engine, Request("old", quotas: [new("quotas/standard", 1)], units: ["S1"]));

        var result = await engine.ExchangeAsync(new InventoryExchange("claims/old", new InventoryReturn("upgrade:1", ReturnReason.Other),
            Request("new", quotas: [new("quotas/premium", 1)], units: ["S1"])));

        result.Succeeded.Should().BeTrue();
        (await engine.GetUnitStatesAsync(["S1"]))["S1"].Should().Be(UnitState.Sold);
        (await engine.GetAvailabilityAsync("quotas/standard")).Sold.Should().Be(0);
        (await engine.GetAvailabilityAsync("quotas/premium")).Sold.Should().Be(1);
        (await engine.GetClaimAsync("claims/new"))!.Units.Should().BeEquivalentTo(new[] { "S1" });

        // The seat belongs to the new claim now: releasing it frees the seat, releasing the old one does nothing.
        (await engine.ReleaseAsync("claims/old", ReturnReason.Cancelled)).Succeeded.Should().BeTrue();
        (await engine.GetUnitStatesAsync(["S1"]))["S1"].Should().Be(UnitState.Sold);
        (await engine.ReleaseAsync("claims/new", ReturnReason.Cancelled)).Succeeded.Should().BeTrue();
        (await engine.GetUnitStatesAsync(["S1"]))["S1"].Should().Be(UnitState.Free);
    }

    [Fact]
    public async Task An_exchange_within_a_sold_out_quota_reuses_what_it_returns()
    {
        var engine = Engine();
        await engine.CreateQuotaAsync("quotas/full", 2, 1);
        await SoldAsync(engine, Request("old", quotas: [new("quotas/full", 2)]));

        var result = await engine.ExchangeAsync(new InventoryExchange("claims/old", new InventoryReturn("change:1", ReturnReason.Other, Quotas: [new("quotas/full", 1)]),
            Request("new", quotas: [new("quotas/full", 1)])));

        result.Succeeded.Should().BeTrue();
        (await engine.GetAvailabilityAsync("quotas/full")).Should().Be(new QuotaAvailability("quotas/full", 2, 2, 0, 0));
        (await engine.GetClaimAsync("claims/old"))!.State.Should().Be(ClaimState.Converted); // one unit left on it
    }

    [Fact]
    public async Task An_exchange_is_idempotent_and_moves_per_buyer_counts()
    {
        var engine = Engine();
        await engine.CreateQuotaAsync("quotas/q", 10, 1);
        var limit = new LimitLine("sales/1/account/u1", 2, 2);
        await SoldAsync(engine, Request("old", quotas: [new("quotas/q", 2)], limits: [limit]));
        var exchange = new InventoryExchange("claims/old", new InventoryReturn("change:1", ReturnReason.Other),
            Request("new", quotas: [new("quotas/q", 2)], limits: [limit]));

        // At the limit already: only because the return gives the two back first does the target fit.
        (await engine.ExchangeAsync(exchange)).Succeeded.Should().BeTrue();
        (await engine.ExchangeAsync(exchange)).Succeeded.Should().BeTrue();
        (await engine.GetAvailabilityAsync("quotas/q")).Sold.Should().Be(2);
        (await engine.TryClaimAsync(Request("third", quotas: [new("quotas/q", 1)], limits: [limit with { N = 1 }]))).Status.Should().Be(ClaimStatus.LimitExceeded);
    }

    [Fact]
    public async Task Seats_given_back_are_not_chain_filled_again_in_the_same_exchange_but_become_holes()
    {
        var engine = Engine();
        await engine.DefineChainAsync("chains/c", [["S1", "S2", "S3", "S4"]]);
        (await engine.TryClaimAsync(Request("old", chains: [new("chains/c", 2)]))).Units.Should().BeEquivalentTo(new[] { "S1", "S2" });
        (await engine.ConvertAsync("claims/old")).Succeeded.Should().BeTrue();

        var result = await engine.ExchangeAsync(new InventoryExchange("claims/old", new InventoryReturn("change:1", ReturnReason.Other),
            Request("new", chains: [new("chains/c", 2)])));

        result.Units.Should().BeEquivalentTo(new[] { "S3", "S4" });
        (await engine.TryClaimAsync(Request("next", chains: [new("chains/c", 2)]))).Units.Should().BeEquivalentTo(new[] { "S1", "S2" });
    }

    [Fact]
    public async Task Every_commit_names_the_units_and_quotas_it_touched()
    {
        var engine = Engine();
        await engine.CreateQuotaAsync("quotas/q", 10, 1);
        commits.Clear();

        (await engine.TryClaimAsync(Request("a", quotas: [new("quotas/q", 1)], units: ["S1"]))).Succeeded.Should().BeTrue();
        (await engine.ConvertAsync("claims/a")).Succeeded.Should().BeTrue();
        (await engine.ExchangeAsync(new InventoryExchange("claims/a", new InventoryReturn("change:1", ReturnReason.Other),
            Request("b", quotas: [new("quotas/q", 1)], units: ["S2"])))).Succeeded.Should().BeTrue();
        (await engine.ReleaseAsync("claims/b", ReturnReason.Refund)).Succeeded.Should().BeTrue();

        commits.Select(c => c.Operation).Should().Equal(InventoryOperations.Claim, InventoryOperations.Convert, InventoryOperations.Exchange, InventoryOperations.Release);
        commits[0].Units.Should().BeEquivalentTo(new[] { "S1" });
        commits[0].Quotas.Should().BeEquivalentTo(new[] { "quotas/q" });
        commits[1].Units.Should().BeEquivalentTo(new[] { "S1" }); // held turned sold
        commits[1].Quotas.Should().BeEmpty();                     // availability did not move
        commits[2].Units.Should().BeEquivalentTo(new[] { "S1", "S2" });
        commits[2].ClaimIds.Should().Equal("claims/a", "claims/b");
        commits[3].Units.Should().BeEquivalentTo(new[] { "S2" });

        // A refused claim commits nothing, so nobody is told.
        var before = commits.Count;
        (await engine.TryClaimAsync(Request("c", units: ["S1"]))).Succeeded.Should().BeTrue();
        (await engine.TryClaimAsync(Request("d", units: ["S1"]))).Status.Should().Be(ClaimStatus.UnitTaken);
        commits.Should().HaveCount(before + 1);
    }

    [Fact]
    public async Task The_item_mapping_is_readable_without_loading_engine_documents()
    {
        var engine = Engine();
        await engine.MapItemAsync("items/weekend", [new("quotas/day-1"), new("quotas/day-2", Multiplier: 2), new("quotas/day-1")]);

        (await engine.GetItemMappingAsync("items/weekend")).Should().BeEquivalentTo(new[] { new ItemQuota("quotas/day-1", 2), new ItemQuota("quotas/day-2", 2) });
        (await engine.GetItemMappingAsync("items/unknown")).Should().BeNull();
    }

    private sealed class TestClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset now = start;

        public override DateTimeOffset GetUtcNow() => now;
    }
}
