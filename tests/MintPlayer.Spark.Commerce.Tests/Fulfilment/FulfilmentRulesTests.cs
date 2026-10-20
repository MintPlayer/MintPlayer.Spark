using MintPlayer.Spark.Commerce.Checkout.Refunds;
using MintPlayer.Spark.Commerce.Fulfilment;
using MintPlayer.Spark.Commerce.Fulfilment.Models;
using MintPlayer.Spark.Commerce.Fulfilment.Services;
using MintPlayer.Spark.Commerce.Shipping.Models;
using MintPlayer.Spark.Commerce.Shop;
using MintPlayer.Spark.Commerce.Shop.Models;
using MintPlayer.Spark.Fulfilment.Contracts;
using MintPlayer.Spark.Replication.Abstractions.Configuration;

namespace MintPlayer.Spark.Commerce.Tests.Fulfilment;

/// <summary>The pure rules of the shop side: the capacity formula (§4.4), ids unique across shops, the contract mapping.</summary>
public class FulfilmentRulesTests
{
    private static FulfilmentStockLevel Level(decimal onHand, params (string Channel, decimal Reserved, decimal Shipped)[] channels) => new()
    {
        OwnerId = "shop-a",
        Sku = "OIL-75",
        OnHandSellable = onHand,
        Channels = channels.Select(c => new FulfilmentChannelStock { Channel = c.Channel, ReservedOtherChannels = c.Reserved, ShippedTotal = c.Shipped }).ToList(),
    };

    // ---- §4.4: capacity = OnHandSellable − ReservedOtherChannels − buffer + ShippedTotal (+ baseline, rule 2) ----------

    [Fact]
    public void A_shipment_is_neutral_and_a_receipt_or_restock_raises_the_capacity()
    {
        // 10 on hand, 3 of this channel shipped so far.
        FulfilmentStock.Capacity(Level(10, ("shop", 0, 3)), "shop", buffer: 0, baseline: 0).Should().Be(13);
        // Ship-confirm of 2 (one warehouse transaction): on hand −2, shipped +2.
        FulfilmentStock.Capacity(Level(8, ("shop", 0, 5)), "shop", 0, 0).Should().Be(13);
        // A restocked return: +1 (rule 4: the restock, not the refund, gives the unit back).
        FulfilmentStock.Capacity(Level(9, ("shop", 0, 5)), "shop", 0, 0).Should().Be(14);
    }

    [Fact]
    public void Other_channels_reservations_the_buffer_and_the_onboarding_baseline_count()
    {
        var level = Level(20, ("shop", 4, 6), ("market", 1, 0));
        FulfilmentStock.Capacity(level, "shop", buffer: 2, baseline: 0).Should().Be(20 - 4 - 2 + 6);
        FulfilmentStock.Capacity(level, "SHOP", buffer: 0, baseline: 15).Should().Be(20 - 4 + 6 + 15); // channels match case-insensitively
        FulfilmentStock.Capacity(level, "market", 0, 0).Should().Be(19);
    }

    [Fact]
    public void Shrinkage_below_zero_is_zero_and_a_missing_channel_stops_selling()
    {
        FulfilmentStock.Capacity(Level(1, ("shop", 5, 0)), "shop", 0, 0).Should().Be(0);
        FulfilmentStock.Capacity(Level(10, ("market", 0, 0)), "shop", 0, 0).HasValue.Should().BeFalse();
        // No channel figures at all: nothing reserved, nothing shipped yet.
        FulfilmentStock.Capacity(Level(7.9m), "shop", 0, 0).Should().Be(7);
    }

    // ---- ids (commerce §4.9: unique across shops) -----------------------------------------------------------------------

    [Fact]
    public void Every_published_id_starts_with_the_shop_and_leads_back_to_its_link()
    {
        var order = FulfilmentIds.Order("shop-a", "SO-1", "Organizations/7-A", 2);
        order.Should().Be("FulfilmentOrders/shop-a/SO-1/Organizations/7-A/2");
        FulfilmentIds.LinkOfOrder("shop-a", order).Should().Be(FulfilmentIds.Link("shop-a", "SO-1", "Organizations/7-A"));
        FulfilmentIds.LinkOfOrder("shop-a", FulfilmentIds.Order("shop-a", "SO-1", null, 1)).Should().Be("FulfilmentLinks/shop-a/SO-1/platform");

        var expected = FulfilmentIds.ExpectedReturn("shop-a", "SO-1", null, "w-123", 1);
        FulfilmentIds.LinkOfExpectedReturn("shop-a", expected).Should().Be("FulfilmentLinks/shop-a/SO-1/platform");

        // Another shop's documents (a warehouse without a row filter) lead nowhere here.
        FulfilmentIds.LinkOfOrder("shop-a", FulfilmentIds.Order("shop-b", "SO-1", null, 1)).Should().BeNull();
        FulfilmentIds.LinkOfOrder("shop-a", "FulfilmentOrders/shop-a/x").Should().BeNull();
        FulfilmentIds.LinkOfOrder("shop-a", null).Should().BeNull();
    }

    [Fact]
    public void Owners_default_to_the_seller_and_the_platform_to_the_shop()
    {
        var options = new SparkCommerceFulfilmentOptions { ShopName = "shop-a", OwnerIds = { ["Organizations/7-A"] = "acme" } };
        options.OwnerOf(null).Should().Be("shop-a");
        options.OwnerOf("Organizations/7-A").Should().Be("acme");
        options.OwnerOf("Organizations/8-A").Should().Be("Organizations/8-A");
        options.PlatformOwnerId = "platform-goods";
        options.OwnerOf(null).Should().Be("platform-goods");
    }

    // ---- the contract mapping ---------------------------------------------------------------------------------------

    [Fact]
    public void The_return_reason_travels_one_to_one_GA8()
    {
        foreach (var reason in Enum.GetValues<ReturnReasonCode>())
            FulfilmentContractBuilder.Reason(reason).ToString().Should().Be(reason.ToString());
    }

    [Fact]
    public void Checkout_records_restock_only_when_every_line_was_restocked()
    {
        static FulfilmentReturnReceiptLine L(FulfilmentReturnDisposition d) => new() { Sku = "X", Quantity = 1, Disposition = d };
        FulfilmentContractBuilder.Disposition([L(FulfilmentReturnDisposition.Restock), L(FulfilmentReturnDisposition.Restock)]).Should().Be(ReturnDisposition.Restock);
        FulfilmentContractBuilder.Disposition([L(FulfilmentReturnDisposition.Restock), L(FulfilmentReturnDisposition.Quarantine)]).Should().Be(ReturnDisposition.Quarantine);
        FulfilmentContractBuilder.Disposition([L(FulfilmentReturnDisposition.Quarantine), L(FulfilmentReturnDisposition.Scrap)]).Should().Be(ReturnDisposition.Dispose);
        FulfilmentContractBuilder.Disposition([L(FulfilmentReturnDisposition.ReturnToVendor)]).Should().Be(ReturnDisposition.Dispose);
    }

    [Fact]
    public void The_ship_to_address_is_plain_text_and_a_service_point_carries_its_own_street()
    {
        var shopOrder = new ShopOrder
        {
            ShipTo = new ShippingAddress { Name = "A. Buyer", Street = "Kerkstraat", HouseNumber = "1", Addition = "b", PostalCode = "1000", City = "Brussel", Country = " be ", Phone = "+32 2 000 00 00" },
        };
        var home = FulfilmentContractBuilder.Address(shopOrder)!;
        home.Line1.Should().Be("Kerkstraat 1 b");
        home.CountryCode.Should().Be("BE");
        home.ServicePointId.Should().BeNull();

        shopOrder.ServicePoint = new ShipmentServicePoint { Id = "BPOST-123", Name = "Shop", Address = new ShippingAddress { Street = "Markt", HouseNumber = "5", PostalCode = "2000", City = "Antwerpen", Country = "BE" } };
        var point = FulfilmentContractBuilder.Address(shopOrder)!;
        point.Name.Should().Be("A. Buyer");
        point.Line1.Should().Be("Markt 5");
        point.City.Should().Be("Antwerpen");
        point.ServicePointId.Should().Be("BPOST-123");
        point.Phone.Should().Be("+32 2 000 00 00");

        FulfilmentContractBuilder.Address(new ShopOrder()).Should().BeNull();
    }

    // ---- startup ------------------------------------------------------------------------------------------------------

    [Fact]
    public void Startup_refuses_a_missing_shop_name_shop_only_mode_and_unbound_warehouse_collections()
    {
        var replication = new SparkReplicationOptions { ModuleName = "Shop", ModuleUrl = "https://shop" };
        var problems = SparkCommerceFulfilmentExtensions.Problems(new SparkCommerceFulfilmentOptions(), new SparkCommerceShopOptions(), replication, hasPublisher: true);
        problems.Should().Contain(p => p.Contains("ShopName"));
        problems.Should().Contain(p => p.Contains("Warehouse"));
        problems.Should().Contain(p => p.Contains("FulfilmentOrderStatuses") && p.Contains("FulfilmentReturnReceipts"));

        foreach (var collection in SparkCommerceFulfilmentExtensions.ConsumedCollections)
            replication.Sources[collection] = ["Warehouse"];
        SparkCommerceFulfilmentExtensions.Problems(new SparkCommerceFulfilmentOptions { ShopName = "shop-a" }, new SparkCommerceShopOptions { Fulfilment = ShopFulfilmentMode.Warehouse }, replication, hasPublisher: true)
            .Should().BeEmpty();
        SparkCommerceFulfilmentExtensions.Problems(new SparkCommerceFulfilmentOptions { ShopName = "shop a/1" }, new SparkCommerceShopOptions { Fulfilment = ShopFulfilmentMode.Warehouse }, replication, hasPublisher: false)
            .Should().HaveCount(2);
    }
}
