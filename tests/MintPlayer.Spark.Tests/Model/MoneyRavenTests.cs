using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Linq;

namespace MintPlayer.Spark.Tests.Model;

/// <summary>
/// <see cref="Money"/> through a real RavenDB store configured the way production is
/// (<c>ApplySparkConventions</c>): the stored shape, the round trip, and the two query paths
/// <c>MoneyQueryFields</c> relies on — nested members on a collection query, companions on a static index.
/// </summary>
public class MoneyRavenTests : SparkTestDriver
{
    public sealed class Product
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public Money? Price { get; set; }
    }

    public sealed class ProductsByPrice : AbstractIndexCreationTask<Product, ProductsByPrice.Row>
    {
        public sealed class Row
        {
            public string? Name { get; set; }
            public Money? Price { get; set; }
            [IgnoreProperty] public decimal? PriceAmount { get; set; }
            [IgnoreProperty] public string? PriceCurrency { get; set; }
        }

        public ProductsByPrice()
        {
            Map = products => from p in products
                              select new Row
                              {
                                  Name = p.Name,
                                  Price = p.Price,
                                  PriceAmount = p.Price!.Amount,
                                  PriceCurrency = p.Price!.Currency,
                              };
            Index(r => r.Price, FieldIndexing.No);
            StoreAllFields(FieldStorage.Yes);
        }
    }

    private async Task SeedAsync()
    {
        using var session = Store.OpenAsyncSession();
        await session.StoreAsync(new Product { Id = "products/b", Name = "b", Price = new Money(9.50m, "EUR") });
        await session.StoreAsync(new Product { Id = "products/a", Name = "a", Price = new Money(12.30m, "EUR") });
        await session.StoreAsync(new Product { Id = "products/c", Name = "c", Price = new Money(5m, "USD") });
        await session.StoreAsync(new Product { Id = "products/d", Name = "d", Price = new Money(1230m, "JPY") });
        await session.SaveChangesAsync();
    }

    [Fact]
    public async Task Money_is_stored_as_amount_and_currency_with_the_CLR_names_and_loads_back_exactly()
    {
        await SeedAsync();

        using (var session = Store.OpenAsyncSession())
        {
            var loaded = await session.LoadAsync<Product>("products/a");
            loaded.Price.Should().Be(new Money(12.30m, "EUR"));
            loaded.Price!.Amount.Should().Be(12.30m);
        }

        // The same document read without the converter: what is actually on disk.
        using (var session = Store.OpenAsyncSession())
        {
            var raw = await session.LoadAsync<RawProduct>("products/a");
            raw.Price!.Properties().Select(p => p.Name).Should().Equal("Amount", "Currency");
            raw.Price.Value<decimal>("Amount").Should().Be(12.30m);
            raw.Price.Value<string>("Currency").Should().Be("EUR");
        }
    }

    public sealed class RawProduct
    {
        public Newtonsoft.Json.Linq.JObject? Price { get; set; }
    }

    [Fact]
    public async Task A_collection_query_filters_and_sorts_by_the_nested_members()
    {
        await SeedAsync();

        using var session = Store.OpenAsyncSession();
        var eur = await session.Query<Product>()
            .Customize(c => c.WaitForNonStaleResults())
            .Where(p => p.Price!.Currency == "EUR" && p.Price.Amount == 12.30m)
            .ToListAsync();
        eur.Select(p => p.Name).Should().Equal("a");

        var ordered = await session.Query<Product>()
            .Customize(c => c.WaitForNonStaleResults())
            .OrderBy(p => p.Price!.Currency)
            .ThenBy(p => p.Price!.Amount)
            .ToListAsync();
        ordered.Select(p => p.Name).Should().Equal("b", "a", "d", "c");
    }

    [Fact]
    public async Task A_static_index_sorts_and_filters_through_the_companions()
    {
        await new ProductsByPrice().ExecuteAsync(Store);
        await SeedAsync();

        using var session = Store.OpenAsyncSession();
        var rows = await session.Query<ProductsByPrice.Row, ProductsByPrice>()
            .Customize(c => c.WaitForNonStaleResults())
            .Where(r => r.PriceCurrency == "EUR")
            .OrderByDescending(r => r.PriceAmount)
            .ProjectInto<ProductsByPrice.Row>()
            .ToListAsync();

        rows.Select(r => r.Name).Should().Equal("a", "b");
        rows[0].Price.Should().Be(new Money(12.30m, "EUR"));
    }
}
