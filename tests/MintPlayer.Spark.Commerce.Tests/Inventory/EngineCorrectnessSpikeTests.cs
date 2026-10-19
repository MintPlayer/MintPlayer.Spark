using System.Collections.Concurrent;
using System.Diagnostics;
using MintPlayer.Spark.Claims;
using MintPlayer.Spark.Commerce.Inventory;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents;
using Raven.Client.Documents.Operations.CompareExchange;
using Xunit.Abstractions;

namespace MintPlayer.Spark.Commerce.Tests.Inventory;

/// <summary>
/// Spike <b>S1</b> (commerce PRD §7): does any interleaving oversell? Each run builds a random scenario (quotas with
/// random shard counts, a hot set of seats, per-buyer limits), lets claimants claim, extend, convert and release
/// concurrently with short expiries while a sweeper runs and is killed halfway, then checks I1–I5 against the ledger.
/// <para>
/// CI runs <c>3 × 100</c>; the PRD figures are <c>SPARK_SPIKE_S1_RUNS=100 SPARK_SPIKE_S1_CLAIMANTS=1000</c>.
/// </para>
/// </summary>
public class EngineCorrectnessSpikeTests(ITestOutputHelper output) : SparkTestDriver
{
    private static int Runs => int.TryParse(Environment.GetEnvironmentVariable("SPARK_SPIKE_S1_RUNS"), out var n) ? n : 3;
    private static int Claimants => int.TryParse(Environment.GetEnvironmentVariable("SPARK_SPIKE_S1_CLAIMANTS"), out var n) ? n : 100;

    private static readonly TimeSpan Grace = TimeSpan.FromMilliseconds(300);

    [Fact]
    public async Task No_interleaving_oversells()
    {
        var total = Stopwatch.StartNew();
        var outcomes = new ConcurrentDictionary<ClaimStatus, int>();
        for (var run = 0; run < Runs; run++)
            await RunOnceAsync(run, outcomes);
        output.WriteLine($"S1: {Runs} runs x {Claimants} claimants in {total.Elapsed.TotalSeconds:F1}s; outcomes: "
            + string.Join(", ", outcomes.OrderBy(o => o.Key).Select(o => $"{o.Key}={o.Value}")));
    }

    private async Task RunOnceAsync(int run, ConcurrentDictionary<ClaimStatus, int> outcomes)
    {
        var random = new Random(run * 7919 + 17);
        var engine = new InventoryEngine(Store, options: new InventoryEngineOptions { Grace = Grace, MaxAttempts = 50 });
        var prefix = $"r{run}";

        var quotas = Enumerable.Range(0, 3).Select(q => (Id: $"quotas/{prefix}-{q}", Capacity: random.Next(20, 150), Shards: new[] { 1, 4, 8 }[random.Next(3)])).ToArray();
        foreach (var (id, capacity, shards) in quotas)
            await engine.CreateQuotaAsync(id, capacity, shards);
        var seats = Enumerable.Range(0, 60).Select(s => $"{prefix}/seats/{s}").ToArray();
        var buyers = Enumerable.Range(0, Math.Max(10, Claimants / 3)).Select(b => $"{prefix}/account/{b}").ToArray();
        const int perBuyer = 8;

        using var sweeperStop = new CancellationTokenSource();
        var sweeper = Task.Run(async () =>
        {
            while (!sweeperStop.IsCancellationRequested)
            {
                foreach (var quota in quotas)
                    await engine.SweepAsync(quota.Id, CancellationToken.None);
                try { await Task.Delay(50, sweeperStop.Token); } catch (OperationCanceledException) { }
            }
        });

        var claimIds = new ConcurrentBag<(string Id, ClaimStatus Status)>();
        var claimants = Enumerable.Range(0, Claimants).Select(c => Task.Run(async () =>
        {
            var rnd = new Random(run * 100_003 + c);
            var id = $"claims/{prefix}-{c}";
            var lines = quotas.Where(_ => rnd.Next(3) > 0).Select(q => new QuotaLine(q.Id, rnd.Next(1, 4))).ToList();
            if (lines.Count == 0) lines.Add(new QuotaLine(quotas[0].Id, 1));
            var units = rnd.Next(2) == 0 ? [] : Enumerable.Range(0, rnd.Next(1, 3)).Select(_ => seats[rnd.Next(seats.Length)]).Distinct().ToList();
            var n = lines.Sum(l => l.N);
            var expiresAt = DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(rnd.Next(100, 1500));
            var request = new ClaimRequest(id, $"baskets/{prefix}-{c}", expiresAt, lines, units, [new LimitLine(buyers[rnd.Next(buyers.Length)], perBuyer, n)]);

            var result = await engine.TryClaimAsync(request);
            claimIds.Add((id, result.Status));
            outcomes.AddOrUpdate(result.Status, 1, (_, v) => v + 1);
            if (!result.Succeeded) return;

            await Task.Delay(rnd.Next(0, 400));
            switch (rnd.Next(5))
            {
                case 0: await engine.ConvertAsync(id); break;
                case 1: await engine.ReleaseAsync(id); break;
                case 2:
                    await engine.ExtendAsync(id, DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(500));
                    await engine.ConvertAsync(id);
                    break;
                case 3:
                    if ((await engine.ConvertAsync(id)).Succeeded)
                    {
                        await engine.ReleaseAsync(id, returnStock: rnd.Next(2) == 0);
                        await engine.ReleaseAsync(id); // a duplicate refund: must change nothing (I4)
                    }
                    break;
                default: break; // abandoned: lapses on its own
            }
        })).ToArray();

        // Kill the sweeper halfway through the claimants (I3).
        await Task.WhenAny(Task.WhenAll(claimants), Task.Delay(300));
        sweeperStop.Cancel();
        await sweeper;
        await Task.WhenAll(claimants);

        await AssertInvariantsAsync(quotas.Select(q => q.Id).ToArray(), seats, buyers, perBuyer, claimIds.ToArray());
    }

    private async Task AssertInvariantsAsync(string[] quotaIds, string[] seats, string[] buyers, int perBuyer, (string Id, ClaimStatus Status)[] attempts)
    {
        using var session = Store.OpenAsyncSession();
        var claims = (await session.LoadAsync<InventoryClaim>(attempts.Select(a => a.Id))).Values.OfType<InventoryClaim>().ToDictionary(c => c.Id!);

        // I2: a refused claim leaves nothing behind.
        foreach (var (id, status) in attempts.Where(a => a.Status != ClaimStatus.Held))
            claims.ContainsKey(id).Should().BeFalse($"refused claim {id} ({status}) must not exist");

        var now = DateTimeOffset.UtcNow;
        bool live(DateTimeOffset? e) => e is null || e.Value + Grace > now;

        foreach (var quotaId in quotaIds)
        {
            var quota = await session.LoadAsync<InventoryQuota>(quotaId);
            var shards = (await session.LoadAsync<InventoryQuotaShard>(Enumerable.Range(0, quota.ShardCount).Select(k => InventoryEngine.ShardId(quotaId, k)))).Values.ToList();
            shards.Sum(s => s!.CapacityShare).Should().Be(quota.Capacity);
            foreach (var shard in shards)
            {
                // I1, per shard and therefore per quota.
                (shard!.Sold + shard.Holds.Where(h => live(h.ExpiresAt)).Sum(h => h.N)).Should().BeLessThanOrEqualTo(shard.CapacityShare, $"{shard.Id} oversold");
                // The ledger: sold equals the converted claims' allocations, minus those released with their stock.
                var expectedSold = claims.Values
                    .Where(c => c.State == ClaimState.Converted)
                    .SelectMany(c => c.Allocations).Where(a => a.ShardId == shard.Id).Sum(a => a.N);
                var keptByShipped = claims.Values
                    .Where(c => c.State == ClaimState.Released)
                    .SelectMany(c => c.Allocations).Where(a => a.ShardId == shard.Id).Sum(a => a.N);
                shard.Sold.Should().BeGreaterThanOrEqualTo(expectedSold, $"{shard.Id} lost sold units");
                shard.Sold.Should().BeLessThanOrEqualTo(expectedSold + keptByShipped, $"{shard.Id} counts units no claim sold");
                // Every hold belongs to a held claim.
                foreach (var hold in shard.Holds)
                    claims.TryGetValue(hold.ClaimId, out var owner).Should().BeTrue($"hold of unknown claim {hold.ClaimId}");
            }
        }

        // Seats: a seat is held or sold by at most one claim, and that claim says so.
        var operations = Store.Operations;
        var unitValues = await operations.SendAsync(new GetCompareExchangeValuesOperation<SparkClaimHold>(seats.Select(InventoryEngine.UnitKey).ToArray()));
        foreach (var (key, value) in unitValues)
        {
            if (value?.Value is null) continue;
            claims.TryGetValue(value.Value.Owner, out var owner).Should().BeTrue($"{key} names unknown claim {value.Value.Owner}");
            owner!.Units.Select(InventoryEngine.UnitKey).Should().Contain(key);
            owner.State.Should().NotBe(ClaimState.Released, $"{key} still held by released claim {owner.Id}");
            if (owner.State == ClaimState.Converted) value.Value.ExpiresAtUtc.HasValue.Should().BeFalse($"{key} sold but still expiring");
        }
        foreach (var claim in claims.Values.Where(c => c.State == ClaimState.Converted))
            foreach (var unit in claim.Units)
                unitValues[InventoryEngine.UnitKey(unit)].Value!.Owner.Should().Be(claim.Id, $"sold seat {unit} taken over");
        var soldSeats = claims.Values.Where(c => c.State == ClaimState.Converted).SelectMany(c => c.Units).ToList();
        soldSeats.Should().OnlyHaveUniqueItems();

        // Per-buyer limits: the count is exactly the converted claims' units, and never above the limit.
        var limitValues = await operations.SendAsync(new GetCompareExchangeValuesOperation<BuyerCount>(buyers.Select(InventoryEngine.LimitKey).ToArray()));
        foreach (var buyer in buyers)
        {
            var count = limitValues.TryGetValue(InventoryEngine.LimitKey(buyer), out var v) ? v?.Value : null;
            var expected = claims.Values.Where(c => c.State == ClaimState.Converted).SelectMany(c => c.Limits).Where(l => l.Key == buyer).Sum(l => l.N);
            (count?.Count ?? 0).Should().Be(expected, $"buyer {buyer} count");
            ((count?.Count ?? 0) + (count?.Holds.Where(h => live(h.ExpiresAt)).Sum(h => h.N) ?? 0)).Should().BeLessThanOrEqualTo(perBuyer);
        }
    }
}
