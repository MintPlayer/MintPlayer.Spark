using System.Linq.Expressions;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Commerce.Shop.Models;

namespace MintPlayer.Spark.Commerce.Shop.Indexes;

/// <summary>
/// The shop's index declarations (X19): the application's generator emits the index classes, so indexes live in the
/// application (owner rule) and every host gets the same ones. Queried by name.
/// <list type="table">
/// <item><term><c>ShopProducts/Catalog</c></term><description>The storefront catalogue (GB13): published state,
/// seller, categories with their ancestors, facet terms, the lowest price, and <see cref="ShopCatalogEntry.Text"/>, a
/// <c>[Search]</c> field, so the index runs on Lucene and search is typo-tolerant (X18, D38). Facet counts come from
/// this map (RavenDB facets over <see cref="ShopCatalogEntry.Facets"/> and <see cref="ShopCatalogEntry.Categories"/>),
/// never from distinct values.</description></item>
/// </list>
/// </summary>
public static class ShopIndexes
{
    public const string Catalog = "ShopProducts/Catalog";

    [SparkIndexMap("ShopProducts_Catalog")]
    public static Expression<Func<IEnumerable<ShopProduct>, IEnumerable<ShopCatalogEntry>>> Products { get; } =
        products => from product in products
                    select new ShopCatalogEntry
                    {
                        IsPublished = product.IsPublished,
                        OrganizationId = product.OrganizationId,
                        Channel = product.Channel,
                        Slug = product.Slug,
                        Categories = product.CategoryPath,
                        Facets = product.FacetValues,
                        Text = product.SearchText,
                        PriceFrom = product.PriceFromMinor,
                        Currency = product.Currency,
                        Rating = product.RatingAverage,
                        PublishedAt = product.PublishedAt,
                        UpdatedAt = product.UpdatedAt,
                    };
}

/// <summary>One catalogue entry of <see cref="ShopIndexes.Catalog"/>.</summary>
public sealed class ShopCatalogEntry
{
    public bool IsPublished { get; set; }

    public string? OrganizationId { get; set; }

    public string Channel { get; set; } = "";

    public string Slug { get; set; } = "";

    public List<string> Categories { get; set; } = [];

    /// <summary>The facet terms <c>{key}:{value}</c>.</summary>
    public List<string> Facets { get; set; } = [];

    /// <summary>Every language of the name, the brand, the MPN, the SKUs and GTINs: searchable, typo-tolerant (D38).</summary>
    [Search]
    public List<string> Text { get; set; } = [];

    /// <summary>The lowest active variant price in minor units.</summary>
    public long? PriceFrom { get; set; }

    public string? Currency { get; set; }

    public decimal? Rating { get; set; }

    public DateTimeOffset? PublishedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
