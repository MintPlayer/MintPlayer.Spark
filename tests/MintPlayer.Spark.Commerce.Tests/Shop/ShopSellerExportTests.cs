using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Commerce.Shipping.Models;
using MintPlayer.Spark.Commerce.Shop.Models;
using MintPlayer.Spark.Commerce.Shop.Services;
using MintPlayer.Spark.Testing;

namespace MintPlayer.Spark.Commerce.Tests.Shop;

/// <summary>
/// GB14 (PRD §4.15): the shop's seller exports. Each source returns the seller's own rows only, one value per column,
/// and keeps platform identifiers (the review author's account id) out.
/// </summary>
public class ShopSellerExportTests : SparkTestDriver
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-10-19T09:00:00Z");

    private static async Task<List<IReadOnlyList<string?>>> RowsAsync(ISellerExportSource source, string organizationId, DateTimeOffset? from = null, DateTimeOffset? to = null)
    {
        var rows = new List<IReadOnlyList<string?>>();
        await foreach (var row in source.GetRowsAsync(organizationId, from, to))
            rows.Add(row);
        return rows;
    }

    private static ShopOrder Order(string id, string? seller, DateTimeOffset at) => new()
    {
        Id = id, OrganizationId = seller, OrderId = "CheckoutOrders/" + id, OrderNumber = id, Currency = "EUR", CreatedAt = at, UpdatedAt = at,
        Method = "home", Carrier = "test",
        ShipTo = new ShippingAddress { Name = "A. Buyer", Street = "Kerkstraat", HouseNumber = "2", PostalCode = "1000", City = "Brussel", Country = "BE" },
        Lines =
        [
            new ShopOrderLine { LineId = "l1", ItemId = "shop/mug", Sku = "MUG-1", Title = TranslatedString.Of("en", "Mug"), Quantity = 2, UnitPrice = new Money(9.99m, "EUR"), ShippedQuantity = 2, ShippedAt = at.AddDays(1) },
            new ShopOrderLine { LineId = "l2", ItemId = "shop/cap", Sku = "CAP-1", Quantity = 1, UnitPrice = new Money(15m, "EUR"), Cancelled = true },
        ],
    };

    [Fact]
    public async Task The_shop_orders_export_has_one_row_per_line_of_the_sellers_own_orders_in_the_period()
    {
        using (var session = Store.OpenAsyncSession())
        {
            await session.StoreAsync(Order("ShopOrders/1", "organizations/a", T0));
            await session.StoreAsync(Order("ShopOrders/2", "organizations/b", T0));
            await session.StoreAsync(Order("ShopOrders/3", "organizations/a", T0.AddDays(-30)));
            await session.SaveChangesAsync();
        }
        var source = new ShopOrdersExport(Store);
        source.Kind.Should().Be("shop-orders");

        var rows = await RowsAsync(source, "organizations/a", from: T0.AddDays(-1));

        rows.Should().HaveCount(2); // ShopOrders/1's two lines; organizations/b and the older order are out
        rows.Should().OnlyContain(r => r.Count == source.Columns.Count);
        var column = source.Columns.Select((c, i) => (c, i)).ToDictionary(x => x.c, x => x.i);
        rows.Select(r => r[column["order"]]).Distinct().Should().Equal("ShopOrders/1");
        var mug = rows.Single(r => r[column["sku"]] == "MUG-1");
        mug[column["item"]].Should().Be("Mug");
        mug[column["quantity"]].Should().Be("2");
        mug[column["unitPrice"]].Should().Be("9.99");
        mug[column["shipToCountry"]].Should().Be("BE");
        rows.Single(r => r[column["sku"]] == "CAP-1")[column["cancelled"]].Should().Be("true");
    }

    [Fact]
    public async Task The_shop_reviews_export_lists_every_status_of_the_sellers_products_without_the_author_id()
    {
        using (var session = Store.OpenAsyncSession())
        {
            await session.StoreAsync(new ShopProductReview
            {
                Id = "ShopProductReviews/1", ProductId = "ShopProducts/mug", OrganizationId = "organizations/a", OrderId = "CheckoutOrders/1", LineId = "l1",
                AuthorId = "users/secret-account", PostedAt = T0, DisplayName = "Anna", Rating = 4, Title = "Fine", Body = "Holds coffee.", Language = "en",
                Status = EShopReviewStatus.Pending,
            });
            await session.StoreAsync(new ShopProductReview
            {
                Id = "ShopProductReviews/2", ProductId = "ShopProducts/cap", OrganizationId = "organizations/b", OrderId = "CheckoutOrders/2", LineId = "l1",
                AuthorId = "users/2", PostedAt = T0, Rating = 1, Body = "Other seller.",
            });
            await session.SaveChangesAsync();
        }
        var source = new ShopReviewsExport(Store);
        source.Kind.Should().Be("shop-reviews");

        var rows = await RowsAsync(source, "organizations/a");

        var row = rows.Should().ContainSingle().Which;
        row.Should().HaveCount(source.Columns.Count);
        var column = source.Columns.Select((c, i) => (c, i)).ToDictionary(x => x.c, x => x.i);
        row[column["review"]].Should().Be("ShopProductReviews/1");
        row[column["rating"]].Should().Be("4");
        row[column["status"]].Should().Be(nameof(EShopReviewStatus.Pending));
        row.Should().NotContain("users/secret-account");
    }
}
