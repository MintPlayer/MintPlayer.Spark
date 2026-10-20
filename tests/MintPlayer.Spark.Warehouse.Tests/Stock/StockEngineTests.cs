using MintPlayer.Spark.Fulfilment.Contracts;
using MintPlayer.Spark.Warehouse.Models;
using MintPlayer.Spark.Warehouse.Stock;

namespace MintPlayer.Spark.Warehouse.Tests.Stock;

/// <summary>The stock engine's rules one command at a time (PRD §4.5, WI1–WI6). The concurrent run is <see cref="StockInvariantGuardTests"/>.</summary>
public class StockEngineTests : WarehouseTestBase
{
    private string _shelf = "", _receiving = "", _quarantine = "", _item = "";

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        await SeedSiteAsync();
        _shelf = await LocationAsync("A-01", WarehouseLocationType.Storage, walk: 10);
        _receiving = await LocationAsync("RCV", WarehouseLocationType.Receiving);
        _quarantine = await LocationAsync("Q-01", WarehouseLocationType.Storage, zoneId: WarehouseZone.IdFor(Site, "Q"));
        _item = await ItemAsync(OwnerA, "SKU-1");
    }

    private string Balance(string location, string? lot = null) => StockBalance.IdFor(location, _item, lot);

    [Fact]
    public async Task A_receipt_creates_the_balance_the_movement_and_the_sellable_level_in_one_commit()
    {
        var result = await StockEngine.ReceiveAsync(new ReceiveStock(Op("op-1"), _shelf, _item, 10));

        result.Outcome.Should().Be(StockOutcome.Applied);
        (await LoadAsync<StockBalance>(Balance(_shelf)))!.OnHand.Should().Be(10);
        var movement = await LoadAsync<StockMovement>(StockMovement.IdFor("op-1"));
        movement!.Kind.Should().Be(StockMovementKind.Receive);
        movement.UserId.Should().Be("users/staff-1");
        movement.DeviceId.Should().Be("device-1");
        (await LoadAsync<StockLevel>(StockLevel.IdFor(OwnerA, _item)))!.OnHandSellable.Should().Be(10);
    }

    /// <summary>WI4: the op id is the movement id; a replay changes nothing.</summary>
    [Fact]
    public async Task A_replayed_op_id_is_applied_once()
    {
        await StockEngine.ReceiveAsync(new ReceiveStock(Op("op-1"), _shelf, _item, 10));

        var replay = await StockEngine.ReceiveAsync(new ReceiveStock(Op("op-1"), _shelf, _item, 10));

        replay.Outcome.Should().Be(StockOutcome.Replayed);
        (await LoadAsync<StockBalance>(Balance(_shelf)))!.OnHand.Should().Be(10);
    }

    [Fact]
    public async Task An_op_id_reused_for_another_command_is_refused()
    {
        await StockEngine.ReceiveAsync(new ReceiveStock(Op("op-1"), _shelf, _item, 10));

        var other = await StockEngine.AdjustAsync(new AdjustStock(Op("op-1"), _shelf, _item, null, -1, "COUNT"));

        other.Refusal.Should().Be(StockRefusal.OpIdReused);
    }

    /// <summary>Q-W5: receiving and quarantine-zone stock is on hand but not sellable; putting it away makes it sellable.</summary>
    [Fact]
    public async Task Sellable_stock_follows_the_location_type_and_zone()
    {
        await StockEngine.ReceiveAsync(new ReceiveStock(Op("op-1"), _receiving, _item, 6));
        (await LoadAsync<StockLevel>(StockLevel.IdFor(OwnerA, _item)))!.OnHandSellable.Should().Be(0);

        await StockEngine.MoveAsync(new MoveStock(Op("op-2"), Balance(_receiving), _shelf, 4));
        (await LoadAsync<StockLevel>(StockLevel.IdFor(OwnerA, _item)))!.OnHandSellable.Should().Be(4);

        await StockEngine.MoveAsync(new MoveStock(Op("op-3"), Balance(_shelf), _quarantine, 1));
        (await LoadAsync<StockLevel>(StockLevel.IdFor(OwnerA, _item)))!.OnHandSellable.Should().Be(3);
    }

    /// <summary>WI1 and WI2: never negative, never more allocated than on hand, allocated stock cannot be moved away.</summary>
    [Fact]
    public async Task On_hand_never_goes_negative_and_allocation_never_exceeds_it()
    {
        await StockEngine.ReceiveAsync(new ReceiveStock(Op("op-1"), _shelf, _item, 5));

        (await StockEngine.AdjustAsync(new AdjustStock(Op("op-2"), _shelf, _item, null, -6, "COUNT"))).Refusal.Should().Be(StockRefusal.InsufficientStock);
        (await StockEngine.AllocateAsync(new AllocateStock(Op("op-3"), Balance(_shelf), 6))).Refusal.Should().Be(StockRefusal.InsufficientAvailable);
        (await StockEngine.AllocateAsync(new AllocateStock(Op("op-4"), Balance(_shelf), 4))).Outcome.Should().Be(StockOutcome.Applied);
        (await StockEngine.MoveAsync(new MoveStock(Op("op-5"), Balance(_shelf), _receiving, 2))).Refusal.Should().Be(StockRefusal.InsufficientAvailable);
        (await StockEngine.AdjustAsync(new AdjustStock(Op("op-6"), _shelf, _item, null, -2, "COUNT"))).Refusal.Should().Be(StockRefusal.InsufficientAvailable);
        (await StockEngine.AllocateAsync(new AllocateStock(Op("op-7"), Balance(_shelf), -5))).Refusal.Should().Be(StockRefusal.InsufficientAvailable);

        var balance = await LoadAsync<StockBalance>(Balance(_shelf));
        balance!.OnHand.Should().Be(5);
        balance.Allocated.Should().Be(4);
    }

    [Fact]
    public async Task An_adjustment_needs_a_known_reason_code()
    {
        await StockEngine.ReceiveAsync(new ReceiveStock(Op("op-1"), _shelf, _item, 5));

        (await StockEngine.AdjustAsync(new AdjustStock(Op("op-2"), _shelf, _item, null, -1, "NOPE"))).Refusal.Should().Be(StockRefusal.ReasonRequired);
        (await StockEngine.AdjustAsync(new AdjustStock(Op("op-3"), _shelf, _item, null, -1, "../x"))).Refusal.Should().Be(StockRefusal.ReasonRequired);
        var applied = await StockEngine.AdjustAsync(new AdjustStock(Op("op-4"), _shelf, _item, null, -1, "COUNT"));

        applied.Outcome.Should().Be(StockOutcome.Applied);
        (await LoadAsync<StockMovement>(StockMovement.IdFor("op-4")))!.ReasonCode.Should().Be("COUNT");
    }

    /// <summary>WI6: at ship-confirm on hand goes down and the channel's shipped total up, in the same document.</summary>
    [Fact]
    public async Task Shipping_moves_sellable_on_hand_into_the_channels_shipped_total_and_publishes_per_linked_shop()
    {
        using (var session = Store.OpenAsyncSession())
        {
            await session.StoreAsync(new WarehouseOwnerLink { Id = WarehouseOwnerLink.IdFor("ShopA", "a"), SourceModule = "ShopA", ExternalOwnerId = "a", OrganizationId = OwnerA }, WarehouseOwnerLink.IdFor("ShopA", "a"));
            await session.SaveChangesAsync();
        }
        await StockEngine.ReceiveAsync(new ReceiveStock(Op("op-1"), _shelf, _item, 5));
        await StockEngine.ChangeOpenAsync(new ChangeOpenQuantity(Op("op-2"), OwnerA, _item, "web", 3));
        await StockEngine.ChangeOpenAsync(new ChangeOpenQuantity(Op("op-3"), OwnerA, _item, "marketplace", 1));
        await StockEngine.AllocateAsync(new AllocateStock(Op("op-4"), Balance(_shelf), 2));

        (await StockEngine.ShipAsync(new ShipStock(Op("op-5"), Balance(_shelf), 3, "web"))).Refusal.Should().Be(StockRefusal.InsufficientAvailable, "only allocated stock ships");
        (await StockEngine.ShipAsync(new ShipStock(Op("op-6"), Balance(_shelf), 2, "web"))).Outcome.Should().Be(StockOutcome.Applied);

        var level = await LoadAsync<StockLevel>(StockLevel.IdFor(OwnerA, _item));
        level!.OnHandSellable.Should().Be(3);
        var web = level.Channels.Single(c => c.Channel == "web");
        web.ShippedTotal.Should().Be(2);
        web.Open.Should().Be(1);

        var published = await LoadAsync<FulfilmentStockLevel>(StockLevelPublisher.PublishedIdFor("a", "SKU-1"));
        published!.OwnerId.Should().Be("a", "the shop's own owner id, never the warehouse organization");
        published.OnHandSellable.Should().Be(3);
        published.Version.Should().Be(level.Version);
        published.Channels.Single(c => c.Channel == "web").ReservedOtherChannels.Should().Be(1);
        published.Channels.Single(c => c.Channel == "marketplace").ReservedOtherChannels.Should().Be(1);
        Replication.Marked.Should().Contain((typeof(FulfilmentStockLevel), StockLevelPublisher.PublishedIdFor("a", "SKU-1")));
    }

    [Fact]
    public async Task A_lot_tracked_item_needs_a_lot_and_an_untracked_one_refuses_it()
    {
        var tracked = await ItemAsync(OwnerA, "SKU-LOT", ItemTracking.LotAndExpiry);

        (await StockEngine.ReceiveAsync(new ReceiveStock(Op("op-1"), _shelf, tracked, 1))).Refusal.Should().Be(StockRefusal.LotMismatch);
        (await StockEngine.ReceiveAsync(new ReceiveStock(Op("op-2"), _shelf, _item, 1, "L1"))).Refusal.Should().Be(StockRefusal.LotMismatch);
        (await StockEngine.ReceiveAsync(new ReceiveStock(Op("op-3"), _shelf, tracked, 1, "L1", new DateOnly(2027, 1, 31)))).Outcome.Should().Be(StockOutcome.Applied);

        var lot = await LoadAsync<StockLot>(StockLot.IdFor(tracked, "L1"));
        lot!.Expiry.Should().Be(new DateOnly(2027, 1, 31));
        (await LoadAsync<StockBalance>(StockBalance.IdFor(_shelf, tracked, "L1")))!.Expiry.Should().Be(new DateOnly(2027, 1, 31));
    }

    /// <summary>Round 5 (GA7): a recalled lot is blocked at once and leaves the sellable stock in the same commit.</summary>
    [Fact]
    public async Task A_recalled_lot_is_blocked_and_no_longer_sellable()
    {
        var tracked = await ItemAsync(OwnerA, "SKU-LOT", ItemTracking.LotAndExpiry);
        await StockEngine.ReceiveAsync(new ReceiveStock(Op("op-1"), _shelf, tracked, 4, "L1"));
        await WaitForIndexesAsync();

        (await StockEngine.ChangeLotStatusAsync(new ChangeLotStatus(Op("op-2"), StockLot.IdFor(tracked, "L1"), StockLotStatus.Recalled, "supplier recall"))).Outcome.Should().Be(StockOutcome.Applied);

        (await LoadAsync<StockLevel>(StockLevel.IdFor(OwnerA, tracked)))!.OnHandSellable.Should().Be(0);
        (await LoadAsync<StockBalance>(StockBalance.IdFor(_shelf, tracked, "L1")))!.Sellable.Should().BeFalse();
        (await StockEngine.AllocateAsync(new AllocateStock(Op("op-3"), StockBalance.IdFor(_shelf, tracked, "L1"), 1))).Refusal.Should().Be(StockRefusal.LotBlocked);
        (await StockEngine.PickAsync(new MoveStock(Op("op-4"), StockBalance.IdFor(_shelf, tracked, "L1"), _receiving, 1))).Refusal.Should().Be(StockRefusal.LotBlocked);
    }

    [Fact]
    public async Task A_deactivated_location_takes_no_stock_and_a_location_with_history_is_marked()
    {
        await StockEngine.ReceiveAsync(new ReceiveStock(Op("op-1"), _shelf, _item, 1));
        (await LoadAsync<WarehouseLocation>(_shelf))!.HasHistory.Should().BeTrue();

        using (var session = Store.OpenAsyncSession())
        {
            (await session.LoadAsync<WarehouseLocation>(_receiving))!.Active = false;
            await session.SaveChangesAsync();
        }
        (await StockEngine.ReceiveAsync(new ReceiveStock(Op("op-2"), _receiving, _item, 1))).Refusal.Should().Be(StockRefusal.InactiveLocation);
    }
}
