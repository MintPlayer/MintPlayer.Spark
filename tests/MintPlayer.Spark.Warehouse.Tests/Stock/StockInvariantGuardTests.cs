using MintPlayer.Spark.Warehouse.Models;
using MintPlayer.Spark.Warehouse.Stock;
using Xunit.Abstractions;

namespace MintPlayer.Spark.Warehouse.Tests.Stock;

/// <summary>
/// W1 at CI size and the scaled-down concurrency guard the plan keeps in the suite (PRD §7 W1, §14): concurrent workers
/// run a deterministic, seeded random mix of moves, picks, adjustments, allocations, releases and shipments on a few hot
/// balances, re-sending some commands with the same op id, and afterwards every invariant WI1–WI4 and WI6 must hold
/// exactly. The full-scale numbers (≥100 runs × 10k ops, a 3-node cluster, p95 ≤ 150 ms) were dropped in commerce round 4
/// (no load runs on the owner's machine); this records the retry rate it saw instead.
/// </summary>
public class StockInvariantGuardTests(ITestOutputHelper output) : WarehouseTestBase
{
    private const int Seed = 20261020;
    private const int Workers = 6;
    private const int OpsPerWorker = 120;

    private readonly List<string> _locations = [];
    private readonly List<string> _items = [];

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        Options.Stock.MaxAttempts = 50;
        await SeedSiteAsync();
        _locations.Add(await LocationAsync("PF-1", WarehouseLocationType.PickFace, walk: 1));
        _locations.Add(await LocationAsync("PF-2", WarehouseLocationType.PickFace, walk: 2));
        _locations.Add(await LocationAsync("BULK", WarehouseLocationType.Storage, walk: 3));
        _locations.Add(await LocationAsync("RCV", WarehouseLocationType.Receiving, walk: 0));
        _items.Add(await ItemAsync(OwnerA, "HOT-1"));
        _items.Add(await ItemAsync(OwnerA, "HOT-2"));

        foreach (var item in _items)
        foreach (var location in _locations.Take(3))
            (await StockEngine.ReceiveAsync(new ReceiveStock(Op($"seed/{item}/{location}"), location, item, 40))).Outcome.Should().Be(StockOutcome.Applied);
    }

    private sealed record Sent(string OpId, StockResult Result, bool IsResend);

    [Fact]
    public async Task No_interleaving_breaks_WI1_to_WI4_or_WI6()
    {
        var results = await Task.WhenAll(Enumerable.Range(0, Workers).Select(RunWorkerAsync));
        var sent = results.SelectMany(r => r).ToList();

        // WI4: once an op id was applied, every later send of it answers Replayed. (A refused command stores nothing, so
        // its re-send may apply later, when the stock allows it: that is still one application.)
        foreach (var group in sent.GroupBy(s => s.OpId))
        {
            var sends = group.ToList();
            var firstApplied = sends.FindIndex(s => s.Result.Outcome == StockOutcome.Applied);
            sends.Count(s => s.Result.Outcome == StockOutcome.Applied).Should().BeLessThanOrEqualTo(1, group.Key);
            if (firstApplied >= 0)
                sends.Skip(firstApplied + 1).All(s => s.Result.Outcome == StockOutcome.Replayed).Should().BeTrue(group.Key);
        }

        using var session = Store.OpenAsyncSession();
        var movements = await AllAsync<StockMovement>(session, StockMovement.IdPrefix);
        var applied = sent.Where(s => s.Result.Outcome == StockOutcome.Applied).Select(s => s.OpId).ToHashSet();
        movements.Count(m => !m.OpId.StartsWith("seed/", StringComparison.Ordinal)).Should().Be(applied.Count, "one movement per applied op id (WI4)");

        var ledger = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in movements)
        {
            switch (m.Kind)
            {
                case StockMovementKind.Receive:
                    ledger[m.ToBalanceId!] = ledger.GetValueOrDefault(m.ToBalanceId!) + m.Quantity;
                    break;
                case StockMovementKind.Move or StockMovementKind.Pick:
                    ledger[m.FromBalanceId!] = ledger.GetValueOrDefault(m.FromBalanceId!) - m.Quantity;
                    ledger[m.ToBalanceId!] = ledger.GetValueOrDefault(m.ToBalanceId!) + m.Quantity;
                    break;
                case StockMovementKind.Adjust:
                    var id = m.FromBalanceId ?? m.ToBalanceId!;
                    ledger[id] = ledger.GetValueOrDefault(id) + m.Quantity;
                    break;
                case StockMovementKind.Ship:
                    ledger[m.FromBalanceId!] = ledger.GetValueOrDefault(m.FromBalanceId!) - m.Quantity;
                    break;
            }
        }

        var balances = new List<StockBalance>();
        foreach (var location in _locations)
            balances.AddRange(await session.Advanced.LoadStartingWithAsync<StockBalance>(location + "/", pageSize: 1024));
        foreach (var balance in balances)
        {
            balance.OnHand.Should().BeGreaterThanOrEqualTo(0, $"WI1 {balance.Id}");
            balance.Allocated.Should().BeGreaterThanOrEqualTo(0, $"WI2 {balance.Id}");
            balance.Allocated.Should().BeLessThanOrEqualTo(balance.OnHand, $"WI2 {balance.Id}");
            ledger.GetValueOrDefault(balance.Id!).Should().Be(balance.OnHand, $"WI3: the ledger sums to {balance.Id}");
        }

        foreach (var item in _items)
        {
            var level = await session.LoadAsync<StockLevel>(StockLevel.IdFor(OwnerA, item));
            level!.OnHandSellable.Should().Be(balances.Where(b => b.ItemId == item && b.Sellable).Sum(b => b.OnHand), $"WI6 on hand of {item}");
            level.Channels.Sum(c => c.ShippedTotal).Should().Be(movements.Where(m => m.ItemId == item && m.Kind == StockMovementKind.Ship).Sum(m => m.Quantity), $"WI6 shipped of {item}");
        }

        var retried = sent.Count(s => s.Result.Attempts > 1);
        output.WriteLine($"W1 (CI size, seed {Seed}): {sent.Count} commands ({sent.Count(s => s.IsResend)} re-sent), {applied.Count} applied, " +
            $"{sent.Count(s => s.Result.Outcome == StockOutcome.Refused)} refused, {retried} needed a retry ({(double)retried / Math.Max(1, sent.Count):P1}); " +
            $"max attempts {sent.Max(s => s.Result.Attempts)}.");
    }

    private async Task<List<Sent>> RunWorkerAsync(int worker)
    {
        var random = new Random(Seed + worker);
        var stock = StockEngine;
        var sent = new List<Sent>();
        var history = new List<(string OpId, Func<StockOp, Task<StockResult>> Run)>();

        for (var n = 0; n < OpsPerWorker; n++)
        {
            if (history.Count > 0 && random.NextDouble() < 0.1)
            {
                // A lost answer: the same command again, with the same op id.
                var (opId, again) = history[random.Next(history.Count)];
                sent.Add(new Sent(opId, await RunAsync(() => again(Op(opId))), IsResend: true));
                continue;
            }

            var item = _items[random.Next(_items.Count)];
            var from = _locations[random.Next(3)];
            var to = _locations[random.Next(_locations.Count)];
            var quantity = random.Next(1, 6);
            var balance = StockBalance.IdFor(from, item, null);
            Func<StockOp, Task<StockResult>> command = random.Next(7) switch
            {
                0 => op => stock.MoveAsync(new MoveStock(op, balance, to, quantity)),
                1 => op => stock.PickAsync(new MoveStock(op, balance, to, quantity, CarryAllocated: Math.Min(quantity, 1))),
                2 => op => stock.AdjustAsync(new AdjustStock(op, from, item, null, random.Next(2) == 0 ? -quantity : quantity, "COUNT")),
                3 => op => stock.AllocateAsync(new AllocateStock(op, balance, quantity)),
                4 => op => stock.AllocateAsync(new AllocateStock(op, balance, -quantity)),
                5 => op => stock.ShipAsync(new ShipStock(op, balance, Math.Min(quantity, 2), random.Next(2) == 0 ? "web" : "store")),
                _ => op => stock.ReceiveAsync(new ReceiveStock(op, from, item, quantity)),
            };
            var id = $"w{worker}/op{n}";
            history.Add((id, command));
            sent.Add(new Sent(id, await RunAsync(() => command(Op(id))), IsResend: false));
        }
        return sent;
    }

    /// <summary>A command that still conflicts after every retry applied nothing; it counts as refused for the invariants.</summary>
    private static async Task<StockResult> RunAsync(Func<Task<StockResult>> run)
    {
        try
        {
            return await run();
        }
        catch (StockConcurrencyException exhausted)
        {
            return new StockResult(StockOutcome.Refused, StockMovement.IdFor(exhausted.OpId), Attempts: exhausted.Attempts);
        }
    }

    private static async Task<List<T>> AllAsync<T>(Raven.Client.Documents.Session.IAsyncDocumentSession session, string prefix)
    {
        var all = new List<T>();
        for (var start = 0; ; start += 1024)
        {
            var page = (await session.Advanced.LoadStartingWithAsync<T>(prefix, start: start, pageSize: 1024)).ToList();
            all.AddRange(page);
            if (page.Count < 1024)
                return all;
        }
    }
}
