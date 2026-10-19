using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Commerce.Inventory;
using MintPlayer.Spark.Commerce.Shop.Catalog;
using MintPlayer.Spark.Commerce.Shop.Models;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Commerce.Shop.Services;

/// <summary>
/// Publication (X8, §4.10): what must hold before a product sells (<see cref="ProductRules.Problems"/>, the seller gate,
/// the channel's legal documents) and the provisioning that makes it sell: one quota per variant that tracks its
/// inventory. Also keeps the product's maintained fields (facets, search texts, price from, category path) current.
/// </summary>
internal sealed class ShopPublisher(
    ISparkInventory inventory,
    ISparkSellers sellers,
    ISparkLegalDocuments legal,
    IOptionsMonitor<SparkCommerceOptions> commerce,
    IOptionsMonitor<SparkCommerceShopOptions> options,
    TimeProvider clock)
{
    /// <summary>The variants of <paramref name="productId"/> (tracked by <paramref name="session"/>).</summary>
    public static async Task<List<ShopVariant>> VariantsAsync(IAsyncDocumentSession session, string? productId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(productId))
            return [];
        var variants = await session.Query<ShopVariant>()
            .Customize(c => c.WaitForNonStaleResults())
            .Where(v => v.ProductId == productId)
            .Take(1024)
            .ToListAsync(cancellationToken);
        return variants.OrderBy(v => v.SortOrder).ThenBy(v => v.Sku, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Refuses publication when <see cref="ProductRules.Problems"/>, the seller gate (D21, D43) or the channel's required
    /// legal documents (GA2) say so; otherwise provisions every variant's quota. GA7: a goods product without its
    /// manufacturer data is refused here, at publication, never at startup.
    /// </summary>
    public async Task PublishAsync(IAsyncDocumentSession session, ShopProduct product, IReadOnlyList<ShopVariant> variants, CancellationToken cancellationToken)
    {
        var settings = commerce.CurrentValue;
        var problems = ProductRules.Problems(product, variants, settings);
        if (problems.Count == 0)
        {
            var channel = settings.Channels[product.Channel];
            var gate = await sellers.CanSellAsync(product.OrganizationId, product.Channel, cancellationToken);
            if (!gate.Allowed)
                problems.Add("The seller may not sell in this channel: " + string.Join(", ", gate.Reasons) + ".");
            var missing = await legal.GetMissingAsync(product.OrganizationId, channel.RequiredLegalDocuments, cancellationToken);
            if (missing.Count > 0)
                problems.Add("Required legal documents are not published: " + string.Join(", ", missing) + " (GA2).");
        }
        if (problems.Count > 0)
            throw new SparkValidationException(string.Join(" ", problems), nameof(ShopProduct.IsPublished));

        foreach (var variant in variants)
            await ProvisionAsync(variant, cancellationToken);
        product.PublishedAt ??= clock.GetUtcNow();
    }

    /// <summary>
    /// Makes one variant sellable (idempotent): ShopOnly creates its quota at <see cref="ShopVariant.Capacity"/> or
    /// resizes it (refused below what is sold and held); Warehouse creates an empty quota the stock feed fills
    /// (<see cref="ISparkShopStock"/>). A variant that does not track its inventory has no quota.
    /// </summary>
    public async Task ProvisionAsync(ShopVariant variant, CancellationToken cancellationToken)
    {
        if (!variant.TrackInventory || string.IsNullOrEmpty(variant.Id))
            return;
        var settings = options.CurrentValue;
        var quotaId = ShopQuotas.Variant(variant.Id);
        if (settings.Fulfilment == ShopFulfilmentMode.Warehouse)
        {
            await inventory.CreateQuotaAsync(quotaId, 0, 1, cancellationToken);
        }
        else
        {
            var capacity = Math.Max(0, variant.Capacity);
            await inventory.CreateQuotaAsync(quotaId, capacity, ShopQuotas.Shards(capacity, settings.ShardsPerQuota), cancellationToken);
            var availability = await inventory.GetAvailabilityAsync(quotaId, cancellationToken);
            if (availability.Capacity != capacity)
            {
                var result = await inventory.SetCapacityAsync(quotaId, capacity, cancellationToken);
                if (!result.Succeeded)
                    throw new SparkValidationException(
                        $"Variant {variant.Sku}: the capacity cannot drop below the {availability.Sold + availability.Held} units sold and held.",
                        nameof(ShopVariant.Capacity));
            }
        }
        variant.ProvisionedAt ??= clock.GetUtcNow();
    }

    /// <summary>Recomputes the product's maintained fields from its variants and the category tree.</summary>
    public static async Task RefreshAsync(IAsyncDocumentSession session, ShopProduct product, IReadOnlyList<ShopVariant> variants, CancellationToken cancellationToken)
    {
        product.FacetValues = ProductRules.FacetValues(product, variants);
        product.SearchText = ProductRules.SearchText(product, variants);
        (product.PriceFromMinor, product.Currency) = ProductRules.PriceFrom(product, variants);
        product.PriceVaries = variants.Where(v => v.IsActive).Select(v => ProductRules.PriceOf(product, v)?.ToMinorUnits()).Distinct().Count() > 1;
        product.CategoryPath = await CategoryPathAsync(session, product.CategoryIds, cancellationToken);
    }

    /// <summary>The categories and every ancestor (at most 16 levels; a cycle stops the walk).</summary>
    public static async Task<List<string>> CategoryPathAsync(IAsyncDocumentSession session, IEnumerable<string> categoryIds, CancellationToken cancellationToken)
    {
        var path = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var start in categoryIds.Where(id => !string.IsNullOrEmpty(id)))
        {
            var id = start;
            for (var depth = 0; depth < 16 && id is not null && seen.Add(id); depth++)
            {
                path.Add(id);
                id = (await session.LoadAsync<ShopCategory>(id, cancellationToken))?.ParentId;
            }
        }
        return path;
    }
}

/// <summary>A variant's price as a sellable item, for the price history (GA3): keyed by the checkout item id, which is what Promotions' sale prices and the Omnibus prior price look up.</summary>
internal sealed record ShopPriceItem(string Id, string Channel, string? OrganizationId, Money? Price, int? VatRateBasisPoints) : ISellableItem
{
    string? ISellableItem.Id => Id;

    public PriceBasis PriceBasis => PriceBasis.Gross;

    public static ShopPriceItem For(ShopProduct product, ShopVariant variant, Money? price)
        => new(ShopItemIds.For(variant.Id!), product.Channel, product.OrganizationId, price, product.VatRateBasisPoints);
}
