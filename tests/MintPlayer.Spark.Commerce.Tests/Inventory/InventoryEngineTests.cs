using MintPlayer.Spark.Commerce.Inventory;
using MintPlayer.Spark.Testing;

namespace MintPlayer.Spark.Commerce.Tests.Inventory;

/// <summary>
/// The inventory engine's operations (§4.3, M5) against RavenDB, on a hand-driven clock so expiry needs no waiting:
/// item mappings and bundles, Adjust and re-claim, chain fill, pick your seat, late limits, every return path
/// exactly once (I4), restock, the sweeper's release.
/// </summary>
public class InventoryEngineTests : SparkTestDriver
{
    private readonly TestClock clock = new(new DateTimeOffset(2026, 10, 19, 12, 0, 0, TimeSpan.Zero));

    private InventoryEngine Engine() => new(Store, options: new InventoryEngineOptions { Grace = TimeSpan.FromSeconds(1) }, clock: clock);

    private DateTimeOffset In(int minutes) => clock.GetUtcNow().AddMinutes(minutes);

    private ClaimRequest Request(string id, IReadOnlyList<QuotaLine>? quotas = null, IReadOnlyList<ItemLine>? items = null,
        IReadOnlyList<string>? units = null, IReadOnlyList<LimitLine>? limits = null, IReadOnlyList<ChainLine>? chains = null,
        IReadOnlyList<SeatPick>? picks = null, int minutes = 10)
        => new($"claims/{id}", $"baskets/{id}", In(minutes), quotas ?? [], units, limits, items, chains, picks);

    // ---- quotas and item mappings ----

    [Fact]
    public async Task A_bundle_takes_its_multiplied_units_from_every_quota_or_nothing()
    {
        var engine = Engine();
        await engine.CreateQuotaAsync("quotas/day-1", 10, 2);
        await engine.CreateQuotaAsync("quotas/day-2", 3, 1);
        await engine.MapItemAsync("items/weekend", [new("quotas/day-1"), new("quotas/day-2")]);
        await engine.MapItemAsync("items/family-day-1", [new("quotas/day-1", Multiplier: 4)]);

        (await engine.TryClaimAsync(Request("a", items: [new("items/family-day-1", 2)]))).Succeeded.Should().BeTrue();
        (await engine.GetAvailabilityAsync("quotas/day-1")).Available.Should().Be(2);

        // Day 2 has room for 3, day 1 only for 2: the weekend pass of 3 takes nothing at all (I2).
        var refused = await engine.TryClaimAsync(Request("b", items: [new("items/weekend", 3)]));
        refused.Status.Should().Be(ClaimStatus.SoldOut);
        refused.Subject.Should().Be("quotas/day-1");
        (await engine.GetAvailabilityAsync("quotas/day-2")).Available.Should().Be(3);
        (await engine.GetClaimAsync("claims/b")).Should().BeNull();

        (await engine.TryClaimAsync(Request("c", items: [new("items/weekend", 2)]))).Succeeded.Should().BeTrue();
        (await engine.GetAvailabilityAsync("quotas/day-1")).Available.Should().Be(0);
        (await engine.GetAvailabilityAsync("quotas/day-2")).Available.Should().Be(1);
        (await engine.TryClaimAsync(Request("d", items: [new("items/unknown", 1)]))).Status.Should().Be(ClaimStatus.NotFound);
    }

    [Fact]
    public async Task Capacity_cannot_drop_below_what_is_sold_and_held()
    {
        var engine = Engine();
        await engine.CreateQuotaAsync("quotas/q", 10, 3);
        await engine.CreateQuotaAsync("quotas/q", 99, 1); // idempotent: left alone
        (await engine.TryClaimAsync(Request("a", quotas: [new("quotas/q", 6)]))).Succeeded.Should().BeTrue();

        (await engine.SetCapacityAsync("quotas/q", 5)).Status.Should().Be(ClaimStatus.InvalidState);
        (await engine.SetCapacityAsync("quotas/q", 8)).Succeeded.Should().BeTrue();
        (await engine.GetAvailabilityAsync("quotas/q")).Should().Be(new QuotaAvailability("quotas/q", 8, 0, 6, 2));
    }

    // ---- adjust ----

    [Fact]
    public async Task Adjust_changes_the_quantity_all_or_nothing()
    {
        var engine = Engine();
        await engine.CreateQuotaAsync("quotas/q", 5, 2);
        var limit = new LimitLine("sales/1/account/u1", 4, 2);
        (await engine.TryClaimAsync(Request("a", quotas: [new("quotas/q", 2)], limits: [limit]))).Succeeded.Should().BeTrue();

        (await engine.AdjustAsync(Request("a", quotas: [new("quotas/q", 4)], limits: [limit with { N = 4 }]))).Succeeded.Should().BeTrue();
        (await engine.GetAvailabilityAsync("quotas/q")).Available.Should().Be(1);

        // Over the limit: refused, and the claim keeps its 4.
        (await engine.AdjustAsync(Request("a", quotas: [new("quotas/q", 5)], limits: [limit with { N = 5 }]))).Status.Should().Be(ClaimStatus.LimitExceeded);
        (await engine.GetAvailabilityAsync("quotas/q")).Available.Should().Be(1);

        (await engine.AdjustAsync(Request("a", quotas: [new("quotas/q", 1)], limits: [limit with { N = 1 }]))).Succeeded.Should().BeTrue();
        (await engine.GetAvailabilityAsync("quotas/q")).Available.Should().Be(4);
        (await engine.ConvertAsync("claims/a")).Succeeded.Should().BeTrue();
        (await engine.GetAvailabilityAsync("quotas/q")).Should().Be(new QuotaAvailability("quotas/q", 5, 1, 0, 4));
    }

    [Fact]
    public async Task Adjust_on_a_lapsed_claim_reclaims_what_is_still_free()
    {
        var engine = Engine();
        await engine.CreateQuotaAsync("quotas/q", 2, 1);
        (await engine.TryClaimAsync(Request("late", quotas: [new("quotas/q", 1)], units: ["seats/1"], minutes: 1))).Succeeded.Should().BeTrue();
        clock.Advance(TimeSpan.FromMinutes(2));
        (await engine.ConvertAsync("claims/late")).Status.Should().Be(ClaimStatus.Expired);

        // A late payment (D3): the same units are still free, so the re-claim succeeds and converts.
        (await engine.AdjustAsync(Request("late", quotas: [new("quotas/q", 1)], units: ["seats/1"]))).Succeeded.Should().BeTrue();
        (await engine.ConvertAsync("claims/late")).Succeeded.Should().BeTrue();

        // A second late payer whose seat was taken over meanwhile cannot re-claim it.
        (await engine.TryClaimAsync(Request("slow", quotas: [new("quotas/q", 1)], units: ["seats/2"], minutes: 1))).Succeeded.Should().BeTrue();
        clock.Advance(TimeSpan.FromMinutes(2));
        (await engine.TryClaimAsync(Request("fast", units: ["seats/2"]))).Succeeded.Should().BeTrue();
        (await engine.AdjustAsync(Request("slow", quotas: [new("quotas/q", 1)], units: ["seats/2"]))).Status.Should().Be(ClaimStatus.UnitTaken);
    }

    // ---- seats ----

    [Fact]
    public async Task Chain_fill_seats_groups_together_and_reuses_released_holes()
    {
        var engine = Engine();
        string[][] rows = [["A1", "A2", "A3", "A4"], ["B1", "B2", "B3", "B4"]];
        (await engine.DefineChainAsync("chains/block-a", rows)).Succeeded.Should().BeTrue();

        (await engine.TryClaimAsync(Request("g1", chains: [new("chains/block-a", 3)]))).Units.Should().BeEquivalentTo(new[] { "A1", "A2", "A3" });
        (await engine.TryClaimAsync(Request("g2", chains: [new("chains/block-a", 2)]))).Units.Should().BeEquivalentTo(new[] { "B1", "B2" });
        (await engine.DefineChainAsync("chains/block-a", rows)).Status.Should().Be(ClaimStatus.InvalidState);

        // A single fits the skipped A4; then g1 leaves and a pair gets its seats.
        (await engine.TryClaimAsync(Request("g3", chains: [new("chains/block-a", 1)]))).Units.Should().BeEquivalentTo(new[] { "A4" });
        (await engine.ReleaseAsync("claims/g1", ReturnReason.Cancelled)).Succeeded.Should().BeTrue();
        (await engine.TryClaimAsync(Request("g4", chains: [new("chains/block-a", 2)]))).Units.Should().BeEquivalentTo(new[] { "A1", "A2" });

        (await engine.TryClaimAsync(Request("g5", chains: [new("chains/block-a", 3)]))).Status.Should().Be(ClaimStatus.NotTogether);
        (await engine.GetUnitStatesAsync(["A1", "A3", "B4"])).Should().BeEquivalentTo(new Dictionary<string, UnitState>
        {
            ["A1"] = UnitState.Held, ["A3"] = UnitState.Free, ["B4"] = UnitState.Free,
        });
    }

    [Fact]
    public async Task Chain_fill_skips_a_seat_picked_by_hand()
    {
        var engine = Engine();
        await engine.DefineChainAsync("chains/c", [["S1", "S2", "S3", "S4", "S5"]]);
        (await engine.TryClaimAsync(Request("picker", units: ["S2"]))).Succeeded.Should().BeTrue();
        (await engine.TryClaimAsync(Request("group", chains: [new("chains/c", 2)]))).Units.Should().BeEquivalentTo(new[] { "S3", "S4" });
    }

    [Fact]
    public async Task A_pick_that_leaves_a_single_seat_is_refused()
    {
        var engine = Engine();
        string[] row = ["R1", "R2", "R3", "R4", "R5"];
        (await engine.TryClaimAsync(Request("a", picks: [new SeatPick(row, ["R3"], SingleSeatRule.BetweenTaken)]))).Succeeded.Should().BeTrue();

        var refused = await engine.TryClaimAsync(Request("b", picks: [new SeatPick(row, ["R1"])]));
        refused.Status.Should().Be(ClaimStatus.LeavesSingleSeat);
        refused.Subject.Should().Be("R2");
        (await engine.TryClaimAsync(Request("c", picks: [new SeatPick(row, ["R1", "R2"])]))).Succeeded.Should().BeTrue();
        (await engine.TryClaimAsync(Request("d", picks: [new SeatPick(row, ["R3"])]))).Status.Should().Be(ClaimStatus.UnitTaken);
    }

    // ---- limits ----

    [Fact]
    public async Task A_card_fingerprint_known_at_payment_counts_at_conversion()
    {
        var engine = Engine();
        await engine.CreateQuotaAsync("quotas/q", 20, 1);
        var card = InventoryLimitKeys.For("sales/1", max: 4, n: 3, new BuyerIdentifiers(CardFingerprint: "fp_123"));
        (await engine.TryClaimAsync(Request("first", quotas: [new("quotas/q", 3)]))).Succeeded.Should().BeTrue();
        (await engine.ConvertAsync("claims/first", card)).Succeeded.Should().BeTrue();

        // Another account, same card: the conversion is refused and the claim stays held.
        (await engine.TryClaimAsync(Request("second", quotas: [new("quotas/q", 3)]))).Succeeded.Should().BeTrue();
        var refused = await engine.ConvertAsync("claims/second", card);
        refused.Status.Should().Be(ClaimStatus.LimitExceeded);
        (await engine.GetClaimAsync("claims/second"))!.State.Should().Be(ClaimState.Held);
    }

    // ---- return paths, exactly once (I4) ----

    [Fact]
    public async Task A_lost_dispute_returns_its_tickets_once_by_dispute_id()
    {
        var engine = Engine();
        await engine.CreateQuotaAsync("quotas/q", 10, 2);
        var limit = new LimitLine("sales/1/account/u1", 4, 3);
        (await engine.TryClaimAsync(Request("o", quotas: [new("quotas/q", 3)], units: ["S1", "S2", "S3"], limits: [limit]))).Succeeded.Should().BeTrue();
        (await engine.ConvertAsync("claims/o")).Succeeded.Should().BeTrue();

        // Ticket S1 was used at the door; the dispute revokes the two unused ones.
        var dispute = new InventoryReturn("disputes/dp_1", ReturnReason.DisputeLost, Quotas: [new("quotas/q", 2)], Units: ["S2", "S3"], Limits: [limit with { N = 2 }]);
        (await engine.ReturnAsync("claims/o", dispute)).Succeeded.Should().BeTrue();
        (await engine.ReturnAsync("claims/o", dispute)).Succeeded.Should().BeTrue(); // a redelivered webhook

        (await engine.GetAvailabilityAsync("quotas/q")).Should().Be(new QuotaAvailability("quotas/q", 10, 1, 0, 9));
        (await engine.GetUnitStatesAsync(["S1", "S2"])).Should().BeEquivalentTo(new Dictionary<string, UnitState> { ["S1"] = UnitState.Sold, ["S2"] = UnitState.Free });
        var claim = (await engine.GetClaimAsync("claims/o"))!;
        claim.State.Should().Be(ClaimState.Converted);
        claim.Returns.Should().ContainSingle(r => r.Id == "disputes/dp_1" && r.Reason == ReturnReason.DisputeLost);

        // The buyer may buy two more again: the limit count came back too.
        (await engine.TryClaimAsync(Request("again", quotas: [new("quotas/q", 1)], limits: [limit with { N = 3 }]))).Succeeded.Should().BeTrue();
        (await engine.ReturnAsync("claims/o", new InventoryReturn("disputes/dp_2", ReturnReason.DisputeLost, Units: ["S2"]))).Status.Should().Be(ClaimStatus.InvalidState);
    }

    [Fact]
    public async Task A_withdrawal_from_shipped_goods_returns_stock_only_at_restock()
    {
        var engine = Engine();
        await engine.CreateQuotaAsync("quotas/shirts", 5, 1);
        await engine.MapItemAsync("items/shirt-m", [new("quotas/shirts")]);
        (await engine.TryClaimAsync(Request("o", items: [new("items/shirt-m", 2)]))).Succeeded.Should().BeTrue();
        (await engine.ConvertAsync("claims/o")).Succeeded.Should().BeTrue();

        var withdrawal = new InventoryReturn("withdrawals/w1", ReturnReason.Withdrawal, Items: [new("items/shirt-m", 2)], ReturnStock: false);
        (await engine.ReturnAsync("claims/o", withdrawal)).Succeeded.Should().BeTrue();
        (await engine.GetAvailabilityAsync("quotas/shirts")).Available.Should().Be(3);
        (await engine.GetClaimAsync("claims/o"))!.State.Should().Be(ClaimState.Released);

        (await engine.RestockAsync("claims/o", "withdrawals/w1")).Succeeded.Should().BeTrue();
        (await engine.RestockAsync("claims/o", "withdrawals/w1")).Succeeded.Should().BeTrue();
        (await engine.GetAvailabilityAsync("quotas/shirts")).Available.Should().Be(5);
        (await engine.RestockAsync("claims/o", "withdrawals/none")).Status.Should().Be(ClaimStatus.NotFound);
    }

    [Fact]
    public async Task A_refused_line_and_a_full_release_each_return_once()
    {
        var engine = Engine();
        await engine.CreateQuotaAsync("quotas/q", 10, 4);
        (await engine.TryClaimAsync(Request("o", quotas: [new("quotas/q", 4)]))).Succeeded.Should().BeTrue();
        (await engine.ConvertAsync("claims/o")).Succeeded.Should().BeTrue();

        (await engine.ReturnAsync("claims/o", new InventoryReturn("orderlines/7", ReturnReason.RefusedLine, Quotas: [new("quotas/q", 1)]))).Succeeded.Should().BeTrue();
        (await engine.ReleaseAsync("claims/o", ReturnReason.Refund)).Succeeded.Should().BeTrue();
        (await engine.ReleaseAsync("claims/o", ReturnReason.Refund)).Succeeded.Should().BeTrue();
        (await engine.ReturnAsync("claims/o", new InventoryReturn("orderlines/8", ReturnReason.RefusedLine, Quotas: [new("quotas/q", 1)]))).Status.Should().Be(ClaimStatus.InvalidState);

        (await engine.GetAvailabilityAsync("quotas/q")).Should().Be(new QuotaAvailability("quotas/q", 10, 0, 0, 10));
        (await engine.GetClaimAsync("claims/o"))!.ReleaseReason.Should().Be(ReturnReason.Refund);
    }

    // ---- the sweeper ----

    [Fact]
    public async Task The_sweeper_releases_lapsed_claims_and_frees_their_chain_seats()
    {
        var engine = Engine();
        await engine.DefineChainAsync("chains/c", [["S1", "S2", "S3"]]);
        (await engine.TryClaimAsync(Request("lapsing", chains: [new("chains/c", 2)], minutes: 1))).Succeeded.Should().BeTrue();
        (await engine.TryClaimAsync(Request("live", chains: [new("chains/c", 1)], minutes: 60))).Units.Should().BeEquivalentTo(new[] { "S3" });

        // The declared index (X19) as the application's generator would emit it.
        await new InventoryClaims_Lapsing().ExecuteAsync(Store);
        await WaitForIndexesAsync();
        clock.Advance(TimeSpan.FromMinutes(2));
        var released = await engine.ReleaseLapsedAsync(100);
        released.Select(c => c.Id).Should().BeEquivalentTo(new[] { "claims/lapsing" });
        released[0].ReleaseReason.Should().Be(ReturnReason.Expired);

        // The freed pair is a hole again.
        (await engine.TryClaimAsync(Request("next", chains: [new("chains/c", 2)]))).Units.Should().BeEquivalentTo(new[] { "S1", "S2" });
    }

    private sealed class TestClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset now = start;

        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan by) => now += by;
    }
}
