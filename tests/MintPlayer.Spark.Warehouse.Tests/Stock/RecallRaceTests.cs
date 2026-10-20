using MintPlayer.Spark.Warehouse.Models;
using MintPlayer.Spark.Warehouse.Stock;

namespace MintPlayer.Spark.Warehouse.Tests.Stock;

/// <summary>
/// The recall race (lead decision 2026-10-20): a command that makes units of a lot sellable commits only against the lot
/// status it read. The interleaving is forced: the recall commits between the receipt's reads and its commit. Red before
/// the fix: the receipt committed a sellable balance of the recalled lot (the recall's index query could not see the
/// uncommitted balance), and the stock level counted those units.
/// </summary>
public class RecallRaceTests : WarehouseTestBase
{
    private string _shelf = "", _other = "", _item = "", _lotId = "";

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        await SeedSiteAsync();
        _shelf = await LocationAsync("A-01");
        _other = await LocationAsync("A-02");
        _item = await ItemAsync(OwnerA, "SKU-LOT", ItemTracking.LotAndExpiry);
        (await StockEngine.ReceiveAsync(new ReceiveStock(Op("seed"), _shelf, _item, 4, "L1"))).Outcome.Should().Be(StockOutcome.Applied);
        _lotId = StockLot.IdFor(_item, "L1");
        await WaitForIndexesAsync();
    }

    private async Task RecallNowAsync()
        => (await StockEngine.ChangeLotStatusAsync(new ChangeLotStatus(Op("recall"), _lotId, StockLotStatus.Recalled))).Outcome.Should().Be(StockOutcome.Applied);

    [Fact]
    public async Task A_receipt_racing_a_recall_never_counts_the_recalled_lot_sellable()
    {
        var engine = (WarehouseStock)StockEngine;
        var interleaved = false;
        engine.BeforeSaveForTests = async (op, attempt) =>
        {
            if (op.OpId == "late-receipt" && !interleaved)
            {
                interleaved = true;
                await RecallNowAsync();
            }
        };

        var result = await engine.ReceiveAsync(new ReceiveStock(Op("late-receipt"), _other, _item, 6, "L1"));

        interleaved.Should().BeTrue();
        result.Outcome.Should().Be(StockOutcome.Applied);
        result.Attempts.Should().BeGreaterThan(1, "the recall's commit made the receipt's session conflict");
        (await LoadAsync<StockBalance>(StockBalance.IdFor(_other, _item, "L1")))!.Sellable.Should().BeFalse();
        (await LoadAsync<StockLevel>(StockLevel.IdFor(OwnerA, _item)))!.OnHandSellable.Should().Be(0, "no unit of a recalled lot is sellable");
    }

    [Fact]
    public async Task An_allocation_racing_a_recall_is_refused_on_retry()
    {
        var engine = (WarehouseStock)StockEngine;
        var interleaved = false;
        engine.BeforeSaveForTests = async (op, attempt) =>
        {
            if (op.OpId == "late-allocation" && !interleaved)
            {
                interleaved = true;
                await RecallNowAsync();
            }
        };

        var result = await engine.AllocateAsync(new AllocateStock(Op("late-allocation"), StockBalance.IdFor(_shelf, _item, "L1"), 2));

        result.Refusal.Should().Be(StockRefusal.LotBlocked);
        (await LoadAsync<StockBalance>(StockBalance.IdFor(_shelf, _item, "L1")))!.Allocated.Should().Be(0);
    }

    [Fact]
    public async Task A_move_into_a_sellable_location_racing_a_quarantine_lands_unsellable()
    {
        var engine = (WarehouseStock)StockEngine;
        var interleaved = false;
        engine.BeforeSaveForTests = async (op, attempt) =>
        {
            if (op.OpId == "late-move" && !interleaved)
            {
                interleaved = true;
                (await StockEngine.ChangeLotStatusAsync(new ChangeLotStatus(Op("quarantine"), _lotId, StockLotStatus.Quarantine))).Outcome.Should().Be(StockOutcome.Applied);
            }
        };

        var result = await engine.MoveAsync(new MoveStock(Op("late-move"), StockBalance.IdFor(_shelf, _item, "L1"), _other, 3));

        result.Outcome.Should().Be(StockOutcome.Applied);
        (await LoadAsync<StockBalance>(StockBalance.IdFor(_other, _item, "L1")))!.Sellable.Should().BeFalse();
        (await LoadAsync<StockLevel>(StockLevel.IdFor(OwnerA, _item)))!.OnHandSellable.Should().Be(0);
    }
}
