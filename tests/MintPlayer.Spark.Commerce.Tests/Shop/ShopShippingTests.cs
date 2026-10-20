using MintPlayer.Spark.Commerce.Shop;
using MintPlayer.Spark.Commerce.Shop.Contracts;
using MintPlayer.Spark.Commerce.Shop.Models;
using MintPlayer.Spark.Commerce.Shop.Orders;

namespace MintPlayer.Spark.Commerce.Tests.Shop;

/// <summary>The pure shipping rules of a shop order (M12): which units ship, the states that follow, which lines are delivered.</summary>
public class ShopShippingTests
{
    internal static ShopOrder Order() => new()
    {
        Id = "ShopOrders/CheckoutOrders/1-A/platform",
        OrderId = "CheckoutOrders/1-A",
        OrderNumber = "SO-1",
        State = EShopOrderState.Paid,
        Lines =
        [
            new ShopOrderLine { LineId = "L1", VariantId = "ShopVariants/1-A", Sku = "A", Quantity = 2 },
            new ShopOrderLine { LineId = "L2", VariantId = "ShopVariants/2-A", Sku = "B", Quantity = 1 },
        ],
    };

    [Fact]
    public void Everything_left_ships_when_no_lines_are_named_and_cancelled_lines_never_ship()
    {
        var order = Order();
        order.Lines[1].Cancelled = true;
        order.Lines[0].ShippedQuantity = 1;
        ShopShipping.UnitsToShip(order, null).Should().ContainSingle().Which.Quantity.Should().Be(1);
    }

    [Fact]
    public void Requested_units_are_capped_at_what_is_left()
    {
        var units = ShopShipping.UnitsToShip(Order(), [new SparkShopShipLine("L1", 5), new SparkShopShipLine("nope", 1), new SparkShopShipLine("L2", 0)]);
        units.Should().ContainSingle();
        units[0].LineId.Should().Be("L1");
        units[0].Quantity.Should().Be(2);
    }

    [Fact]
    public void Shipping_part_of_the_order_makes_it_partially_shipped_then_shipped()
    {
        var order = Order();
        order.Lines[0].ShippedQuantity = 2;
        ShopShipping.StateAfterShipping(order).Should().Be(EShopOrderState.PartiallyShipped);
        order.Lines[1].ShippedQuantity = 1;
        ShopShipping.StateAfterShipping(order).Should().Be(EShopOrderState.Shipped);
    }

    [Fact]
    public void A_line_split_over_parcels_is_delivered_with_its_last_parcel()
    {
        // CRD Art. 9(2)(b)(i)/(ii), D44: L1 travels in two shipments; it counts as delivered only when both arrived.
        var order = Order();
        order.Lines[0].ShippedQuantity = 2;
        order.Lines[1].ShippedQuantity = 1;
        var first = new ShopOrderShipment { ShipmentId = "S1", Lines = [new ShopShippedLine { LineId = "L1", Quantity = 1 }, new ShopShippedLine { LineId = "L2", Quantity = 1 }] };
        var second = new ShopOrderShipment { ShipmentId = "S2", Lines = [new ShopShippedLine { LineId = "L1", Quantity = 1 }] };
        order.Shipments.AddRange([first, second]);

        first.DeliveredAt = DateTimeOffset.UnixEpoch;
        ShopShipping.NewlyDelivered(order, first).Select(l => l.LineId).Should().Equal("L2");

        second.DeliveredAt = DateTimeOffset.UnixEpoch;
        ShopShipping.NewlyDelivered(order, second).Select(l => l.LineId).Should().Equal("L1");
    }

    [Fact]
    public void A_line_not_fully_shipped_is_not_delivered()
    {
        var order = Order();
        order.Lines[0].ShippedQuantity = 1;
        var only = new ShopOrderShipment { ShipmentId = "S1", DeliveredAt = DateTimeOffset.UnixEpoch, Lines = [new ShopShippedLine { LineId = "L1", Quantity = 1 }] };
        order.Shipments.Add(only);
        ShopShipping.NewlyDelivered(order, only).Should().BeEmpty();
    }

    [Fact]
    public void Idempotency_keys_keep_only_safe_characters_and_stay_short()
    {
        ShopShipping.Key("shop-ship", "ShopOrders/Checkout Orders/1-A").Should().Be("shop-ship/ShopOrders/Checkout_Orders/1-A");
        var key = ShopShipping.Key("shop-ship", new string('x', 300));
        key.Length.Should().BeLessThanOrEqualTo(120);
        key.Should().StartWith("shop-ship/");
    }
}
