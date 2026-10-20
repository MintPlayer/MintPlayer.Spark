using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Commerce.Shop.Models;
using MintPlayer.Spark.Commerce.Shop.Services;
using MintPlayer.Spark.PageMeta;
using MintPlayer.Spark.Sitemap;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Commerce.Shop.Discovery;

/// <summary>
/// The public product page's server-rendered tags (X6) and schema.org <c>Product</c> data (X22, §4.14): name,
/// description, images, brand, MPN, the GTIN and SKU when the product has one variant, and one <c>Offer</c> per variant at
/// its FinalPrice (§4.4) with its availability. Answers null for anything that is not a published product.
/// </summary>
internal sealed class ShopPageMetaProvider(
    SparkShopCatalog catalog,
    IOptionsMonitor<SparkCommerceShopOptions> options) : ISparkPageMetaProvider
{
    public IEnumerable<string> RoutePatterns => [options.CurrentValue.ProductPagePath];

    public async Task<SparkPageMeta?> GetAsync(SparkPageMetaContext context, CancellationToken cancellationToken)
    {
        if (!options.CurrentValue.PublicPages || context.RouteValues["slug"] is not string slug)
            return null;
        if (await catalog.GetProductAsync(slug, cancellationToken) is not { } product)
            return null;
        var languages = context.HttpContext.Request.GetTypedHeaders().AcceptLanguage
            .OrderByDescending(l => l.Quality ?? 1).Select(l => l.Value.Value).OfType<string>().ToList();
        var name = product.Name.Resolve(languages);
        var description = product.Description?.Resolve(languages) is { Length: > 0 } d ? d : name;
        var pageUrl = options.CurrentValue.ProductPagePath.Replace("{slug}", Uri.EscapeDataString(product.Slug), StringComparison.Ordinal);
        var images = product.Media.Select(m => m.Url).Where(u => u.Length > 0).ToList();
        var single = product.Variants.Count == 1 ? product.Variants[0] : null;
        var data = new SparkProductData(
            Name: name,
            Description: description,
            Images: images.Count > 0 ? images : null,
            Sku: single?.Sku,
            Gtin: single?.Gtin,
            Mpn: product.Mpn,
            Brand: product.Brand,
            Offers: product.Variants.Select(v => new SparkOffer(
                v.FinalPrice,
                Availability(v.Availability),
                pageUrl + "?variant=" + Uri.EscapeDataString(v.Id),
                v.Options.Count == 0 ? null : string.Join(" · ", v.Options.Select(o => o.Value)))).ToList());
        Uri? image = images.FirstOrDefault() is { } first && Uri.TryCreate(first, UriKind.RelativeOrAbsolute, out var parsed) ? parsed : null;
        return new SparkPageMeta(name, Truncate(description, 300), image, "product", StructuredData: [data]);
    }

    internal static SparkAvailability Availability(string availability) => availability switch
    {
        nameof(ShopAvailability.OutOfStock) => SparkAvailability.OutOfStock,
        nameof(ShopAvailability.LimitedAvailability) => SparkAvailability.LimitedAvailability,
        _ => SparkAvailability.InStock,
    };

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..(max - 1)].TrimEnd() + "…";
}

/// <summary>Published products in <c>/sitemap.xml</c> (X22), as <c>/sitemaps/products/{page}.xml</c>.</summary>
internal sealed class ShopSitemapSource(IDocumentStore store, IOptionsMonitor<SparkCommerceShopOptions> options) : ISparkSitemapSource
{
    public string Name => "products";

    public async Task<int> CountAsync(SparkSitemapContext context, CancellationToken cancellationToken)
    {
        if (!options.CurrentValue.PublicPages)
            return 0;
        using var session = store.OpenAsyncSession();
        return await session.Query<ShopProduct>().Where(p => p.IsPublished == true).CountAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<SparkSitemapEntry>> GetEntriesAsync(SparkSitemapContext context, int skip, int take, CancellationToken cancellationToken)
    {
        if (!options.CurrentValue.PublicPages)
            return [];
        using var session = store.OpenAsyncSession();
        var page = await session.Query<ShopProduct>()
            .Where(p => p.IsPublished == true)
            .OrderBy(p => p.Id)
            .Skip(skip)
            .Take(take)
            .Select(p => new { p.Slug, p.UpdatedAt })
            .ToListAsync(cancellationToken);
        var path = options.CurrentValue.ProductPagePath;
        return page.Select(p => new SparkSitemapEntry(path.Replace("{slug}", Uri.EscapeDataString(p.Slug), StringComparison.Ordinal), p.UpdatedAt)).ToList();
    }
}

/// <summary>DSA notices (GA5, DSA Art. 16) on products: kind <c>product</c>; the seller is the responsible party.</summary>
internal sealed class ShopDsaTargetResolver(IDocumentStore store) : IDsaNoticeTargetResolver
{
    public IReadOnlyCollection<string> Kinds { get; } = ["product"];

    public async Task<DsaNoticeTarget?> ResolveAsync(string kind, string targetId, CancellationToken cancellationToken = default)
    {
        using var session = store.OpenAsyncSession();
        var product = await session.LoadAsync<ShopProduct>(targetId, cancellationToken);
        return product is { IsPublished: true } ? new DsaNoticeTarget(product.Id!, product.OrganizationId, product.Name.GetValue(TranslatedString.DefaultLanguage) ?? product.Slug) : null;
    }
}

/// <summary>
/// The catalogue's ranking (GA5: CRD Art. 6a(1)(a), P2B Reg. (EU) 2019/1150 Art. 5): with search words by relevance, else
/// newest first; the buyer may sort by price or rating. No paid placement.
/// </summary>
internal sealed class ShopRankingParameters : IRankingParameterSource
{
    public IEnumerable<RankingParameter> GetParameters() =>
    [
        new("commerce.shop", "commerceShop.ranking.relevance", "commerceShop.ranking.relevanceDescription", 1),
        new("commerce.shop", "commerceShop.ranking.newest", "commerceShop.ranking.newestDescription", 2),
        new("commerce.shop", "commerceShop.ranking.buyerSort", "commerceShop.ranking.buyerSortDescription", 3),
    ];
}
