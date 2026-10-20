using MintPlayer.Spark.Fulfilment.Contracts;
using MintPlayer.Spark.Replication.Abstractions;
using MintPlayer.Spark.Warehouse.Barcodes;
using MintPlayer.Spark.Warehouse.Items;
using MintPlayer.Spark.Warehouse.Models;
using MintPlayer.Spark.Warehouse.Owners;
using MintPlayer.Spark.Warehouse.Stock;

namespace MintPlayer.Spark.Warehouse.Tests.Items;

/// <summary>Items (PRD §4.5, WD9; Q-W12): (owner, SKU) unique, pre-items linked without moving stock, catalog replicas consumed.</summary>
public class WarehouseItemTests : WarehouseTestBase
{
    [Fact]
    public async Task Owner_and_SKU_is_a_unique_key_but_two_owners_may_share_a_SKU()
    {
        var first = await Get<IWarehouseItems>().CreateAsync(new WarehouseItem { OrganizationId = OwnerA, Sku = "SKU-1", Name = "one" });
        var duplicate = await Get<IWarehouseItems>().CreateAsync(new WarehouseItem { OrganizationId = OwnerA, Sku = "sku-1 ", Name = "again" });
        var otherOwner = await Get<IWarehouseItems>().CreateAsync(new WarehouseItem { OrganizationId = OwnerB, Sku = "SKU-1", Name = "theirs" });

        first.Succeeded.Should().BeTrue();
        duplicate.Refusal.Should().Be(ItemRefusal.DuplicateSku);
        otherOwner.Succeeded.Should().BeTrue();
        (await Get<IWarehouseItems>().FindAsync(OwnerA, "SKU-1"))!.Id.Should().Be(first.ItemId);
    }

    /// <summary>Q-W12: the link may re-key the SKU; stock keeps pointing at the unchanged item id.</summary>
    [Fact]
    public async Task Linking_a_pre_item_rekeys_its_SKU_and_keeps_its_stock()
    {
        await SeedSiteAsync();
        var shelf = await LocationAsync("A-01");
        var pre = await Get<IWarehouseItems>().CreateAsync(new WarehouseItem { OrganizationId = OwnerA, Sku = "PRE-1", Name = "pre", IsPreItem = true });
        await StockEngine.ReceiveAsync(new ReceiveStock(Op("op-1"), shelf, pre.ItemId!, 7));

        var linked = await Get<IWarehouseItems>().LinkAsync(pre.ItemId!, "FulfilmentCatalogItems/a/SHOP-1", "SHOP-1");

        linked.Succeeded.Should().BeTrue();
        (await Get<IWarehouseItems>().FindAsync(OwnerA, "SHOP-1"))!.Id.Should().Be(pre.ItemId);
        (await Get<IWarehouseItems>().FindAsync(OwnerA, "PRE-1")).Should().BeNull();
        (await LoadAsync<StockBalance>(StockBalance.IdFor(shelf, pre.ItemId!, null)))!.OnHand.Should().Be(7);
        (await Get<IWarehouseItems>().LinkAsync(pre.ItemId!, "x", "SHOP-2")).Refusal.Should().Be(ItemRefusal.NotLinkable, "only a pre-item links");
    }

    [Fact]
    public async Task A_linked_shops_catalog_item_becomes_an_item_with_its_GTIN_and_a_deleted_one_keeps_the_item()
    {
        using (var session = Store.OpenAsyncSession())
        {
            await Get<IWarehouseOwnerLinks>().LinkAsync(session, "ShopA", "a", OwnerA);
            await session.SaveChangesAsync();
        }
        var consumer = (IReplicaChanged<FulfilmentCatalogItem>)ActivatorUtilities(Get<IWarehouseItems>());
        var catalogItem = new FulfilmentCatalogItem { Id = "FulfilmentCatalogItems/a/SHOP-1", OwnerId = "a", Sku = "SHOP-1", Name = "Mug", Gtins = ["5412345000013"] };

        await consumer.OnReplicaChangedAsync(new ReplicaChange<FulfilmentCatalogItem>(catalogItem.Id, "ShopA", null, catalogItem, "A:1"), CancellationToken.None);

        var item = await Get<IWarehouseItems>().FindAsync(OwnerA, "SHOP-1");
        item!.CatalogItemId.Should().Be(catalogItem.Id);
        (await LoadAsync<BarcodeRegistration>(BarcodeRegistration.IdFor("05412345000013", OwnerA, item.Id!))).Should().NotBeNull();

        await consumer.OnReplicaChangedAsync(new ReplicaChange<FulfilmentCatalogItem>(catalogItem.Id, "ShopA", catalogItem, null, "A:2"), CancellationToken.None);
        (await LoadAsync<WarehouseItem>(item.Id!))!.CatalogItemRemoved.Should().BeTrue("a discontinued product keeps its item while stock remains");
    }

    [Fact]
    public async Task An_unlinked_owners_catalog_item_is_ignored()
    {
        var consumer = (IReplicaChanged<FulfilmentCatalogItem>)ActivatorUtilities(Get<IWarehouseItems>());
        var catalogItem = new FulfilmentCatalogItem { Id = "FulfilmentCatalogItems/x/1", OwnerId = "x", Sku = "X-1", Name = "?" };

        await consumer.OnReplicaChangedAsync(new ReplicaChange<FulfilmentCatalogItem>(catalogItem.Id, "ShopX", null, catalogItem, "A:1"), CancellationToken.None);

        using var session = Store.OpenAsyncSession();
        (await session.Advanced.LoadStartingWithAsync<WarehouseItem>(WarehouseItem.IdPrefix)).Should().BeEmpty();
    }

    private CatalogItemConsumer ActivatorUtilities(IWarehouseItems items) => new(Store, Get<IWarehouseOwnerLinks>(), items);
}

/// <summary>The barcode pipeline (PRD §4.6, WD8): GS1 first, own codes, the registry; choices, pre-selection, unknown codes.</summary>
public class BarcodeResolutionTests : WarehouseTestBase
{
    private const string Gtin = "05412345000013";
    private string _itemA1 = "", _itemA2 = "";

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        await SeedSiteAsync();
        await LocationAsync("A-01");
        _itemA1 = await ItemAsync(OwnerA, "SKU-1");
        _itemA2 = await ItemAsync(OwnerA, "SKU-2");
        using var session = Store.OpenAsyncSession();
        foreach (var (item, level, quantity) in new[] { (_itemA1, PackagingLevel.Each, 1m), (_itemA2, PackagingLevel.Case, 12m) })
        {
            var id = BarcodeRegistration.IdFor(Gtin, OwnerA, item);
            await session.StoreAsync(new BarcodeRegistration { Id = id, Code = Gtin, CodeType = BarcodeCodeType.Gtin13, Level = level, Quantity = quantity, OrganizationId = OwnerA, ItemId = item }, id);
        }
        await session.SaveChangesAsync();
    }

    private Task<BarcodeResolution> ResolveAsync(string raw, string? expected = null)
        => Get<IBarcodeResolver>().ResolveAsync(new BarcodeScanContext(raw, Site, [OwnerA], expected));

    [Fact]
    public async Task Several_matches_let_the_user_choose_with_the_expected_item_preselected()
    {
        var resolution = await ResolveAsync("5412345000013", expected: _itemA2);

        resolution.State.Should().Be(BarcodeResolutionState.Choose);
        resolution.Matches.Count.Should().Be(2);
        resolution.Preselected!.TargetId.Should().Be(_itemA2);
        resolution.Preselected.PackQuantity.Should().Be(12, "a case code counts its pack quantity");
    }

    [Fact]
    public async Task A_GS1_element_string_is_parsed_and_its_GTIN_looked_up()
    {
        var resolution = await ResolveAsync("]C1010541234500001310LOT7\u001d17271231");

        resolution.Scan.LotCode.Should().Be("LOT7");
        resolution.Scan.Expiry.Should().Be(new DateOnly(2027, 12, 31));
        resolution.Matches.Count.Should().Be(2);
    }

    [Fact]
    public async Task A_location_code_resolves_to_the_location()
    {
        var resolution = await ResolveAsync("a-01");

        resolution.State.Should().Be(BarcodeResolutionState.Resolved);
        resolution.Matches.Single().Kind.Should().Be(BarcodeTargetKind.Location);
    }

    [Fact]
    public async Task An_unknown_code_offers_link_to_item_only_to_holders_of_the_right()
    {
        var withRight = await Get<IBarcodeResolver>().ResolveAsync(new BarcodeScanContext("NOPE-42", Site, [OwnerA]), canLinkToItem: true);
        var without = await Get<IBarcodeResolver>().ResolveAsync(new BarcodeScanContext("NOPE-42", Site, [OwnerA]));

        withRight.State.Should().Be(BarcodeResolutionState.Unknown);
        withRight.CanLinkToItem.Should().BeTrue();
        without.CanLinkToItem.Should().BeFalse();
    }

    [Fact]
    public async Task A_code_with_a_slash_never_reaches_into_another_id_space()
    {
        var resolution = await ResolveAsync("x/../../warehouse-items/");

        resolution.State.Should().Be(BarcodeResolutionState.Unknown);
    }
}
