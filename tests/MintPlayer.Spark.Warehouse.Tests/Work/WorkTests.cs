using MintPlayer.Spark.Warehouse.Models;
using MintPlayer.Spark.Warehouse.Stock;
using MintPlayer.Spark.Warehouse.Work;

namespace MintPlayer.Spark.Warehouse.Tests.Work;

/// <summary>Min/max replenishment (PRD §4.5, WD5): fixed pick faces only, one task per face, sourced by FEFO.</summary>
public class ReplenishmentTests : WarehouseTestBase
{
    [Fact]
    public async Task A_pick_face_at_its_minimum_gets_one_task_from_the_earliest_expiring_lot()
    {
        await SeedSiteAsync();
        var item = await ItemAsync(OwnerA, "SKU-1", ItemTracking.LotAndExpiry);
        var face = await LocationAsync("PF-1", WarehouseLocationType.PickFace, walk: 1, fixedItemId: item, min: 2, max: 10);
        var bulk1 = await LocationAsync("B-1", walk: 20);
        var bulk2 = await LocationAsync("B-2", walk: 30);
        await StockEngine.ReceiveAsync(new ReceiveStock(Op("r1"), face, item, 2, "L-FACE", new DateOnly(2030, 1, 1)));
        await StockEngine.ReceiveAsync(new ReceiveStock(Op("r2"), bulk1, item, 50, "L-LATE", new DateOnly(2031, 1, 1)));
        await StockEngine.ReceiveAsync(new ReceiveStock(Op("r3"), bulk2, item, 50, "L-EARLY", new DateOnly(2030, 6, 1)));
        await WaitForIndexesAsync();

        var task = await Get<IWarehouseReplenishment>().OnShortPickAsync(face);

        task!.Kind.Should().Be(WarehouseTaskKind.Replenish);
        task.LocationId.Should().Be(bulk2, "FEFO: the earliest removal date first");
        task.LotCode.Should().Be("L-EARLY");
        task.TargetLocationId.Should().Be(face);
        task.Quantity.Should().Be(8);
        (await Get<IWarehouseReplenishment>().CheckAsync(face)).Should().BeNull("one open task per face");
    }

    [Fact]
    public async Task A_face_above_its_minimum_or_without_a_fixed_item_needs_nothing()
    {
        await SeedSiteAsync();
        var item = await ItemAsync(OwnerA, "SKU-1");
        var face = await LocationAsync("PF-1", WarehouseLocationType.PickFace, fixedItemId: item, min: 2, max: 10);
        var loose = await LocationAsync("PF-2", WarehouseLocationType.PickFace);
        await StockEngine.ReceiveAsync(new ReceiveStock(Op("r1"), face, item, 3));

        (await Get<IWarehouseReplenishment>().CheckAsync(face)).Should().BeNull();
        (await Get<IWarehouseReplenishment>().CheckAsync(loose)).Should().BeNull();
    }
}

/// <summary>The task envelope (PRD §4.5): leases, op ids, productivity events.</summary>
public class TaskEnvelopeTests : WarehouseTestBase
{
    private const string TaskId = "warehouse-tasks/t-1";

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        using var session = Store.OpenAsyncSession();
        await session.StoreAsync(new WarehouseTask { Id = TaskId, Kind = WarehouseTaskKind.Pick, SiteId = Site, Quantity = 3, OrganizationIds = [OwnerA] }, TaskId);
        await session.SaveChangesAsync();
    }

    [Fact]
    public async Task A_live_lease_keeps_others_out_and_a_replayed_confirmation_is_a_no_op()
    {
        var tasks = Get<IWarehouseTasks>();
        (await tasks.ClaimAsync(TaskId, "users/a")).Should().Be(TaskClaimOutcome.Claimed);
        (await tasks.ClaimAsync(TaskId, "users/b")).Should().Be(TaskClaimOutcome.HeldByOther);
        (await tasks.ConfirmAsync(TaskId, "users/b", "op-x", 1, finish: false)).Should().Be(TaskConfirmOutcome.NotHolder);

        (await tasks.ConfirmAsync(TaskId, "users/a", "op-1", 2, finish: false)).Should().Be(TaskConfirmOutcome.Confirmed);
        (await tasks.ConfirmAsync(TaskId, "users/a", "op-1", 2, finish: false)).Should().Be(TaskConfirmOutcome.Replayed);
        (await tasks.ConfirmAsync(TaskId, "users/a", "op-2", 1, finish: true)).Should().Be(TaskConfirmOutcome.Confirmed);

        var task = await LoadAsync<WarehouseTask>(TaskId);
        task!.Confirmed.Should().Be(3);
        task.State.Should().Be(WarehouseTaskState.Done);
        var productivity = await LoadAsync<ProductivityEvent>(ProductivityEvent.IdPrefix + Uri.EscapeDataString(TaskId));
        productivity!.UserId.Should().Be("users/a");
        productivity.Quantity.Should().Be(3);
    }
}

/// <summary>Round 5 (commerce GA7): the recall trace lists what shipped from a lot and what is still on hand, owner-scoped and audited.</summary>
public class RecallTraceTests : WarehouseTestBase
{
    [Fact]
    public async Task The_trace_lists_shipment_lines_and_stock_on_hand_and_is_audited()
    {
        await SeedSiteAsync();
        var shelf = await LocationAsync("A-01");
        var item = await ItemAsync(OwnerA, "SKU-1", ItemTracking.LotAndExpiry);
        await StockEngine.ReceiveAsync(new ReceiveStock(new StockOp("r1", Reference: "receipt-9"), shelf, item, 5, "L1"));
        await StockEngine.ReceiveAsync(new ReceiveStock(new StockOp("r2", Reference: "receipt-9"), shelf, item, 5, "L2"));
        var balance = StockBalance.IdFor(shelf, item, "L1");
        await StockEngine.AllocateAsync(new AllocateStock(Op("a1"), balance, 2));
        await StockEngine.ShipAsync(new ShipStock(new StockOp("s1", Reference: "FulfilmentOrders/a/1/shipments/1#1"), balance, 2, "web"));
        await WaitForIndexesAsync();

        var (report, refusal) = await Get<IRecallTrace>().TraceAsync(item, "L1");

        refusal.HasValue.Should().BeFalse();
        report!.Shipments.Single().Reference.Should().Be("FulfilmentOrders/a/1/shipments/1#1");
        report.OnHand.Single().OnHand.Should().Be(3);
        report.SameReceiptLots.Should().BeEquivalentTo(new[] { "L2" });
        System.Text.Encoding.UTF8.GetString(report.ToCsv()).Contains("FulfilmentOrders/a/1/shipments/1#1").Should().BeTrue();
        using var session = Store.OpenAsyncSession();
        (await session.Advanced.LoadStartingWithAsync<RecallTraceRun>(RecallTraceRun.IdPrefix)).Single().UserId.Should().Be("users/staff-1");
    }

    [Fact]
    public async Task Another_owners_member_cannot_trace_the_item()
    {
        var item = await ItemAsync(OwnerA, "SKU-1");
        IsStaff = false;
        UserId = "users/member-of-b";
        Memberships[OwnerB] = MintPlayer.Spark.Organizations.OrganizationRole.Viewer;

        var (report, refusal) = await Get<IRecallTrace>().TraceAsync(item, null);

        report.Should().BeNull();
        refusal.Should().Be(RecallTraceRefusal.NotVisible);
    }
}
