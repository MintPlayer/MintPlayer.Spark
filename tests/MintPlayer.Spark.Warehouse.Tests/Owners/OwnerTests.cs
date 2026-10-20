using MintPlayer.Spark.Fulfilment.Contracts;
using MintPlayer.Spark.Organizations;
using MintPlayer.Spark.Replication.Abstractions;
using MintPlayer.Spark.Replication.Abstractions.Rendering;
using MintPlayer.Spark.Warehouse.Barcodes;
using MintPlayer.Spark.Warehouse.Models;
using MintPlayer.Spark.Warehouse.Owners;
using MintPlayer.Spark.Warehouse.Stock;
using MintPlayer.Spark.Warehouse.Work;

namespace MintPlayer.Spark.Warehouse.Tests.Owners;

/// <summary>
/// Owner links as replication's row filter (Q-W23, PRD §4.2.3 "Row filter"): shop B's module receives only the owner ids
/// linked to it. Red before WM3: there was no filter, every consumer received every row.
/// </summary>
public class OwnerLinkRowFilterTests : WarehouseTestBase
{
    private async Task LinkAsync(string module, string external, string organization)
    {
        using var session = Store.OpenAsyncSession();
        await Get<IWarehouseOwnerLinks>().LinkAsync(session, module, external, organization);
        await session.SaveChangesAsync();
    }

    [Fact]
    public async Task Shop_B_is_scoped_to_its_own_owner_ids_on_every_warehouse_published_contract()
    {
        await LinkAsync("ShopA", "a-1", OwnerA);
        await LinkAsync("ShopB", "b-1", OwnerB);
        await LinkAsync("ShopB", "b-2", OwnerB);
        var filter = Get<IReplicaRowFilter>();

        foreach (var collection in new[] { FulfilmentCollections.OrderStatuses, FulfilmentCollections.Shipments, FulfilmentCollections.StockLevels, FulfilmentCollections.ReturnReceipts })
        {
            var condition = await filter.GetConditionAsync("ShopB", collection, CancellationToken.None);
            condition!.Field.Should().Be("OwnerId");
            condition.AllowedValues.Should().BeEquivalentTo(new[] { "b-1", "b-2" });
            condition.Admits("a-1").Should().BeFalse($"shop A's rows of {collection} never reach shop B");
        }
        (await filter.GetConditionAsync("ShopB", FulfilmentCollections.Orders, CancellationToken.None)).Should().BeNull("the shop → warehouse direction is not filtered here");
    }

    [Fact]
    public async Task An_unlinked_module_receives_nothing_of_the_published_contracts()
    {
        await LinkAsync("ShopA", "a-1", OwnerA);

        var condition = await Get<IReplicaRowFilter>().GetConditionAsync("ShopC", FulfilmentCollections.StockLevels, CancellationToken.None);

        condition!.AllowedValues.Should().BeEmpty();
    }

    [Fact]
    public async Task A_link_change_tells_replication_in_the_same_session_and_an_unlink_narrows_the_scope()
    {
        await LinkAsync("ShopB", "b-1", OwnerB);
        Replication.ScopeChanges.Should().BeEquivalentTo(new[] { "ShopB" });

        using (var session = Store.OpenAsyncSession())
        {
            await Get<IWarehouseOwnerLinks>().UnlinkAsync(session, "ShopB", "b-1");
            await session.SaveChangesAsync();
        }

        Replication.ScopeChanges.Count.Should().Be(2);
        (await Get<IReplicaRowFilter>().GetConditionAsync("ShopB", FulfilmentCollections.Shipments, CancellationToken.None))!.AllowedValues.Should().BeEmpty();
    }

    /// <summary>The filter keys on OwnerId only, so one external owner id may belong to one module: shop B cannot claim shop A's id.</summary>
    [Fact]
    public async Task An_owner_id_linked_to_one_module_cannot_be_linked_to_another()
    {
        await LinkAsync("ShopA", "shared", OwnerA);

        var act = () => LinkAsync("ShopB", "shared", OwnerB);

        await act.Should().ThrowAsync<InvalidOperationException>();
        (await Get<IReplicaRowFilter>().GetConditionAsync("ShopB", FulfilmentCollections.StockLevels, CancellationToken.None))!.AllowedValues.Should().BeEmpty();
    }

    [Fact]
    public async Task An_unlinked_owner_is_not_served()
    {
        await LinkAsync("ShopA", "a-1", OwnerA);

        (await Get<IWarehouseOwnerLinks>().ResolveAsync("ShopA", "a-1"))!.OrganizationId.Should().Be(OwnerA);
        (await Get<IWarehouseOwnerLinks>().ResolveAsync("ShopA", "other")).Should().BeNull("Rejected(OwnerNotServed)");
        (await Get<IWarehouseOwnerLinks>().ResolveAsync("ShopB", "a-1")).Should().BeNull();
    }

    [Fact]
    public void The_rendered_ETL_script_of_shop_B_loads_only_its_owner_ids()
    {
        var manifest = new ReplicaManifest(FulfilmentCollections.StockLevels, FulfilmentCollections.StockLevels, [new ReplicaField("OwnerId", ["OwnerId"]), new ReplicaField("Sku", ["Sku"])]);
        var scope = new ReplicaRowScope([new ReplicaRowCondition(WarehouseOwnerLinkRowFilter.OwnerField, ["b-1"])]);

        var script = ReplicaScriptRenderer.RenderEtlScript(manifest, "Warehouse", scope);

        script.Should().StartWith("if ([\"b-1\"].indexOf(this.OwnerId) >= 0) {");
    }
}

/// <summary>
/// WI5: a member of owner A never reads owner B's documents through the library's queries (raw session queries are not
/// row-filtered, so the library filters explicitly): stock, tasks, barcode choices.
/// </summary>
public class OwnerIsolationTests : WarehouseTestBase
{
    private string _shelf = "", _itemA = "", _itemB = "";

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        await SeedSiteAsync();
        _shelf = await LocationAsync("A-01");
        _itemA = await ItemAsync(OwnerA, "SKU-A");
        _itemB = await ItemAsync(OwnerB, "SKU-B");
        await StockEngine.ReceiveAsync(new ReceiveStock(Op("a"), _shelf, _itemA, 3));
        await StockEngine.ReceiveAsync(new ReceiveStock(Op("b"), _shelf, _itemB, 4));
        using (var session = Store.OpenAsyncSession())
        {
            foreach (var (item, owner) in new[] { (_itemA, OwnerA), (_itemB, OwnerB) })
            {
                var id = BarcodeRegistration.IdFor("05412345000013", owner, item);
                await session.StoreAsync(new BarcodeRegistration { Id = id, Code = "05412345000013", OrganizationId = owner, ItemId = item }, id);
                await session.StoreAsync(new WarehouseTask { Id = WarehouseTask.IdPrefix + owner.Replace('/', '-'), Kind = WarehouseTaskKind.Count, SiteId = Site, OrganizationIds = [owner] });
            }
            await session.SaveChangesAsync();
        }
        await WaitForIndexesAsync();

        IsStaff = false;
        UserId = "users/member-of-a";
        Memberships[OwnerA] = OrganizationRole.Viewer;
    }

    [Fact]
    public async Task A_member_of_owner_A_sees_only_owner_As_stock()
    {
        var owners = await Get<IWarehouseOwnerScope>().VisibleOwnersAsync(nameof(StockBalance));
        owners.Should().BeEquivalentTo(new[] { OwnerA });

        using var session = Store.OpenAsyncSession();
        var rows = await Raven.Client.Documents.LinqExtensions.ToListAsync(StockQueries.Stock(session, owners));
        rows.Select(r => r.OrganizationId).Distinct().Should().BeEquivalentTo(new[] { OwnerA });
        (await StockQueries.CandidatesAsync(session, Site, _itemB, owners, CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_member_of_owner_A_gets_only_owner_As_tasks()
    {
        var tasks = await Get<IWarehouseTasks>().QueueAsync(Site, await Get<IWarehouseOwnerScope>().VisibleOwnersAsync(nameof(WarehouseTask)));

        tasks.SelectMany(t => t.OrganizationIds).Distinct().Should().BeEquivalentTo(new[] { OwnerA });
    }

    [Fact]
    public async Task A_shared_vendor_code_offers_only_the_callers_owners_item()
    {
        var resolution = await Get<IBarcodeResolver>().ResolveAsync(new BarcodeScanContext("5412345000013", Site, [OwnerA]));

        resolution.State.Should().Be(BarcodeResolutionState.Resolved);
        resolution.Matches.Single().TargetId.Should().Be(_itemA);
    }

    [Fact]
    public async Task Staff_see_every_owner()
    {
        IsStaff = true;

        (await Get<IWarehouseOwnerScope>().VisibleOwnersAsync(nameof(StockBalance))).Should().BeNull();
    }
}
