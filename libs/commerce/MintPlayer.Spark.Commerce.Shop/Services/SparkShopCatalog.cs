using System.Globalization;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Commerce.Inventory;
using MintPlayer.Spark.Commerce.Shop.Catalog;
using MintPlayer.Spark.Commerce.Shop.Contracts;
using MintPlayer.Spark.Commerce.Shop.Indexes;
using MintPlayer.Spark.Commerce.Shop.Models;
using MintPlayer.Spark.Organizations.Models;
using Raven.Client.Documents;
using Raven.Client.Documents.Queries.Facets;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Commerce.Shop.Services;

/// <summary>A catalogue question (<c>GET /spark/commerce/shop/catalog</c>).</summary>
/// <param name="Search">The buyer's words; typo-tolerant on the catalogue index (D38).</param>
/// <param name="CategoryId">A category: its products and those of its subcategories.</param>
/// <param name="Facets">Facet terms <c>{key}:{value}</c>: terms of one key widen (OR), keys narrow (AND).</param>
/// <param name="PriceRange">The index of a price range of <c>Facets:PriceRanges</c>.</param>
/// <param name="Sort"><c>relevance</c> (with words), <c>newest</c> (default), <c>price-asc</c>, <c>price-desc</c>, <c>rating</c>.</param>
/// <param name="SellerId">One seller's products.</param>
public sealed record ShopCatalogQuery(
    string? Search = null,
    string? CategoryId = null,
    IReadOnlyList<string>? Facets = null,
    int? PriceRange = null,
    string? Sort = null,
    string? SellerId = null,
    int Skip = 0,
    int Take = 24);

/// <summary>
/// The storefront's reads (PRD §4.10, §4.14): a published product's page, the catalogue with search and facet counts
/// (GB13), the categories. Products are edited as persistent objects by the seller's members (X8); this service serves
/// the public. Everything it answers is published data only.
/// </summary>
public interface ISparkShopCatalog
{
    /// <summary>A published product by its slug, as its page shows it; null when there is none.</summary>
    Task<SparkShopProduct?> GetProductAsync(string slug, CancellationToken cancellationToken = default);

    /// <summary>A page of the catalogue with its facets.</summary>
    Task<SparkShopCatalogPage> SearchAsync(ShopCatalogQuery query, CancellationToken cancellationToken = default);

    /// <summary>Every category, in tree order.</summary>
    Task<IReadOnlyList<SparkShopCategory>> GetCategoriesAsync(CancellationToken cancellationToken = default);
}

internal sealed class SparkShopCatalog(
    IDocumentStore store,
    ISparkInventory inventory,
    IFinalPriceCalculator calculator,
    IOptionsMonitor<SparkCommerceOptions> commerce,
    IOptionsMonitor<SparkCommerceShopOptions> options) : ISparkShopCatalog
{
    // ---- product page ----------------------------------------------------------------------------------------------

    public async Task<SparkShopProduct?> GetProductAsync(string slug, CancellationToken cancellationToken = default)
    {
        using var session = store.OpenAsyncSession();
        if (await PublishedAsync(session, slug, cancellationToken) is not { } product)
            return null;
        var variants = (await ShopPublisher.VariantsAsync(session, product.Id, cancellationToken)).Where(v => v.IsActive).ToList();
        var seller = product.OrganizationId is null ? null : await session.LoadAsync<Organization>(product.OrganizationId, cancellationToken);
        var settings = options.CurrentValue;

        var views = new List<SparkShopVariant>(variants.Count);
        foreach (var variant in variants)
        {
            if (ProductRules.PriceOf(product, variant) is not { } price)
                continue;
            var final = FinalUnit(product, price);
            var (availability, available) = await AvailabilityAsync(variant, cancellationToken);
            views.Add(new SparkShopVariant(
                variant.Id!, ShopItemIds.For(variant.Id!), variant.Sku, variant.Gtin,
                variant.Options.Select(o => new SparkShopOption(o.Name, o.Value)).ToList(),
                final,
                ProductRules.UnitPrice(final, product.UnitOfMeasure, ProductRules.NetQuantityOf(product, variant), product.UnitPriceReference),
                ProductRules.NetQuantityOf(product, variant),
                availability.ToString(),
                available));
        }

        SparkShopSafety? safety = product.Kind == EShopProductKind.Goods
            ? new SparkShopSafety(product.ManufacturerName, product.ManufacturerAddress, product.ManufacturerContact,
                product.ResponsiblePersonName, product.ResponsiblePersonAddress, product.ResponsiblePersonContact,
                product.SafetyIdentifier, variants.Select(v => v.Gtin).OfType<string>().ToList(),
                product.SafetyPictureUrl ?? product.Media.OrderBy(m => m.SortOrder).FirstOrDefault()?.Url, product.SafetyWarnings)
            : null;

        return new SparkShopProduct(
            product.Id!, product.Slug, product.Name, product.Description, product.Brand, product.Mpn, product.Kind.ToString(),
            product.OrganizationId, seller?.Name, product.Channel, views.FirstOrDefault()?.FinalPrice.Currency ?? product.Currency,
            product.MinimumAge, product.CategoryIds,
            product.Media.OrderBy(m => m.SortOrder).Select(m => new SparkShopMedia(m.Url, m.Alt, m.VariantIds)).ToList(),
            product.Attributes.Select(a => new SparkShopOption(a.Name, a.Value)).ToList(),
            views,
            product.UnitOfMeasure.ToString(), ProductRules.UnitSymbol(product.UnitOfMeasure), product.UnitPriceReference,
            safety,
            settings.LegalGuaranteeMonths,
            GuaranteeNotice(settings),
            product.DurabilityGuaranteeMonths is > 0 and var months ? new SparkShopDurability(months, product.DurabilityGuarantor, product.DurabilityGuaranteeTerms) : null,
            product.WithdrawalExemption,
            product.ReviewsEnabled && settings.Reviews.Enabled,
            product.ReviewCount,
            product.RatingAverage);
    }

    /// <summary>GA8: the host's legal-guarantee notice, or null for the client's translation.</summary>
    internal static TranslatedString? GuaranteeNotice(SparkCommerceShopOptions settings)
    {
        if (settings.GuaranteeNotice is not { Count: > 0 } texts)
            return null;
        var notice = new TranslatedString();
        foreach (var (tag, text) in texts.Where(t => !string.IsNullOrWhiteSpace(t.Key) && !string.IsNullOrWhiteSpace(t.Value)))
            notice.Set(tag, text);
        return notice.IsEmpty ? null : notice;
    }

    /// <summary>A published product by its slug (tracked by <paramref name="session"/>).</summary>
    internal static async Task<ShopProduct?> PublishedAsync(IAsyncDocumentSession session, string? slug, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(slug))
            return null;
        return await session.Query<ShopProduct>()
            .Where(p => p.Slug == slug && p.IsPublished == true)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>The FinalPrice of one unit (§4.4): the unit price with every mandatory fee of the channel.</summary>
    internal Money FinalUnit(ShopProduct product, Money price)
        => commerce.CurrentValue.Channels.ContainsKey(product.Channel)
            ? calculator.Calculate(new FinalPriceRequest(product.Channel, price, PriceBasis.Gross, product.VatRateBasisPoints)).UnitTotal
            : price;

    /// <summary>A variant's availability: in stock, a few left (with the count), or out of stock.</summary>
    internal async Task<(ShopAvailability Availability, int? Available)> AvailabilityAsync(ShopVariant variant, CancellationToken cancellationToken)
    {
        if (!variant.TrackInventory)
            return (ShopAvailability.InStock, null);
        if (variant.ProvisionedAt is null)
            return (ShopAvailability.OutOfStock, 0);
        QuotaAvailability quota;
        try
        {
            quota = await inventory.GetAvailabilityAsync(ShopQuotas.Variant(variant.Id!), cancellationToken);
        }
        catch (InvalidOperationException)
        {
            return (ShopAvailability.OutOfStock, 0); // the quota was never provisioned
        }
        if (quota.Available <= 0)
            return (ShopAvailability.OutOfStock, 0);
        return quota.Available <= options.CurrentValue.LowStockThreshold
            ? (ShopAvailability.LimitedAvailability, quota.Available)
            : (ShopAvailability.InStock, null);
    }

    // ---- catalogue -------------------------------------------------------------------------------------------------

    public async Task<SparkShopCatalogPage> SearchAsync(ShopCatalogQuery query, CancellationToken cancellationToken = default)
    {
        var settings = options.CurrentValue;
        var currency = commerce.CurrentValue.DefaultCurrency;
        var take = Math.Clamp(query.Take, 1, settings.Facets.MaxPageSize);
        var skip = Math.Max(0, query.Skip);
        var words = CatalogSearch.Words(query.Search);
        var typo = words.Count > 0 && await SparkSearchEngine.RunsLuceneAsync(store, null, ShopIndexes.Catalog, cancellationToken);
        var selected = (query.Facets ?? []).Where(CatalogSearch.IsFacetTerm).Distinct(StringComparer.Ordinal).Take(32).ToList();
        var ranges = CatalogSearch.Ranges(settings.Facets.EffectivePriceRanges, currency);
        var range = query.PriceRange is { } index && index >= 0 && index < ranges.Count ? ranges[index] : null;

        using var session = store.OpenAsyncSession();
        var filtered = CatalogSearch.Filter(session.Advanced.AsyncDocumentQuery<ShopProduct>(ShopIndexes.Catalog),
            words, typo, query.CategoryId, query.SellerId, selected, range, currency);
        filtered = CatalogSearch.Order(filtered, query.Sort, words.Count > 0);
        var products = await filtered.Statistics(out var statistics).Skip(skip).Take(take).ToListAsync(cancellationToken);

        var facetQuery = CatalogSearch.Filter(session.Advanced.AsyncDocumentQuery<ShopProduct>(ShopIndexes.Catalog),
            words, typo, query.CategoryId, query.SellerId, selected, range, currency);
        var facetResults = await facetQuery.AggregateBy(new List<FacetBase>
        {
            new Facet { FieldName = nameof(ShopCatalogEntry.Facets) },
            new RangeFacet { Ranges = ranges.Select(r => r.Rql).ToList() },
        }).ExecuteAsync(cancellationToken);

        var sellerIds = products.Select(p => p.OrganizationId).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
        Dictionary<string, Organization> sellers = sellerIds.Count == 0 ? new() : await session.LoadAsync<Organization>(sellerIds, cancellationToken);
        var items = products.Select(p =>
        {
            var image = p.Media.OrderBy(m => m.SortOrder).FirstOrDefault(m => m.VariantIds.Count == 0) ?? p.Media.OrderBy(m => m.SortOrder).FirstOrDefault();
            Money? from = p.PriceFromMinor is { } minor && p.Currency is { } c ? FinalUnit(p, Money.FromMinorUnits(minor, c)) : null;
            return new SparkShopCatalogItem(p.Id!, p.Slug, p.Name, p.Brand, image?.Url, image?.Alt, from, p.PriceVaries, p.ReviewCount, p.RatingAverage,
                p.OrganizationId is { } org && sellers.TryGetValue(org, out var seller) ? seller?.Name : null);
        }).ToList();

        return new SparkShopCatalogPage(
            items,
            (int)statistics.TotalResults,
            CatalogSearch.Facets(facetResults, selected),
            CatalogSearch.PriceRanges(facetResults, ranges, query.PriceRange),
            typo);
    }

    // ---- categories ------------------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<SparkShopCategory>> GetCategoriesAsync(CancellationToken cancellationToken = default)
    {
        using var session = store.OpenAsyncSession();
        var categories = await session.Query<ShopCategory>().Take(2048).ToListAsync(cancellationToken);
        return categories
            .OrderBy(c => c.ParentId ?? "", StringComparer.Ordinal).ThenBy(c => c.SortOrder).ThenBy(c => c.Slug, StringComparer.Ordinal)
            .Select(c => new SparkShopCategory(c.Id!, c.Slug, c.Name, c.Description, c.ParentId, c.FacetKeys, c.SortOrder))
            .ToList();
    }
}

/// <summary>A variant's availability (schema.org <c>ItemAvailability</c> names).</summary>
public enum ShopAvailability
{
    InStock,
    LimitedAvailability,
    OutOfStock,
}

/// <summary>A price range of the price facet, in minor units of one currency.</summary>
/// <param name="Rql">The range as RavenDB's range facet takes it, built from numbers only (never from request text).</param>
internal sealed record CatalogPriceRange(long? FromMinor, long? ToMinor, string Currency, string Rql);

/// <summary>
/// The catalogue query, built on the document-query API so the typo-tolerant arm can follow each word (X18, D38):
/// <c>(boost(search(Text, *word*), 2) or fuzzy(Text = word, s))</c> per word, every word required. Every value travels
/// as a query parameter; field names are the index's own constants. The fuzzy arm is added only when the server confirms
/// the index runs Lucene (<see cref="SparkSearchEngine.RunsLuceneAsync"/>); Corax refuses fuzzy. The similarity follows
/// core's rule (one edit up to seven letters, two from eight, none below four).
/// </summary>
internal static class CatalogSearch
{
    private const string Text = nameof(ShopCatalogEntry.Text);
    private const string FacetsField = nameof(ShopCatalogEntry.Facets);
    private const string PriceField = nameof(ShopCatalogEntry.PriceFrom);

    /// <summary>The buyer's words: letters and digits only (every other character separates words), at most eight.</summary>
    public static List<string> Words(string? search)
    {
        if (string.IsNullOrWhiteSpace(search))
            return [];
        var cleaned = new string(search.Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ').ToArray());
        return cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal).Take(8).ToList();
    }

    /// <summary>The minimum similarity for a word, or null when it is matched by substring only (core's rule, X18).</summary>
    public static decimal? Similarity(int length)
    {
        if (length < 4)
            return null;
        var edits = length >= 8 ? 2 : 1;
        var similarity = 1m - (decimal)edits / (length - edits) - 0.01m;
        return Math.Floor(similarity * 100m) / 100m;
    }

    public static bool IsFacetTerm(string? term)
        => term is { Length: > 2 and <= 200 } && term.IndexOf(':') is var colon and > 0 && ProductRules.IsKey(term[..colon]) && colon < term.Length - 1;

    public static List<CatalogPriceRange> Ranges(IReadOnlyList<decimal> boundaries, string currency)
    {
        var digits = Money.MinorUnitDigits(currency);
        var factor = (decimal)Math.Pow(10, digits);
        var minors = boundaries.Select(b => (long)decimal.Round(b * factor)).ToList();
        var ranges = new List<CatalogPriceRange>();
        long? from = null;
        foreach (var to in minors)
        {
            ranges.Add(new CatalogPriceRange(from, to, currency, from is null
                ? $"{PriceField} < {to.ToString(CultureInfo.InvariantCulture)}"
                : $"{PriceField} >= {from.Value.ToString(CultureInfo.InvariantCulture)} and {PriceField} < {to.ToString(CultureInfo.InvariantCulture)}"));
            from = to;
        }
        if (from is not null)
            ranges.Add(new CatalogPriceRange(from, null, currency, $"{PriceField} >= {from.Value.ToString(CultureInfo.InvariantCulture)}"));
        return ranges;
    }

    public static IAsyncDocumentQuery<ShopProduct> Filter(IAsyncDocumentQuery<ShopProduct> query, IReadOnlyList<string> words, bool typo,
        string? categoryId, string? sellerId, IReadOnlyList<string> facets, CatalogPriceRange? range, string currency)
    {
        query = query.WhereEquals(nameof(ShopCatalogEntry.IsPublished), true);
        if (!string.IsNullOrEmpty(categoryId))
            query = query.AndAlso().WhereEquals(nameof(ShopCatalogEntry.Categories), categoryId);
        if (!string.IsNullOrEmpty(sellerId))
            query = query.AndAlso().WhereEquals(nameof(ShopCatalogEntry.OrganizationId), sellerId);
        foreach (var group in facets.GroupBy(f => f[..f.IndexOf(':')], StringComparer.Ordinal))
        {
            query = query.AndAlso().OpenSubclause();
            var first = true;
            foreach (var term in group)
            {
                if (!first)
                    query = query.OrElse();
                query = query.WhereEquals(FacetsField, term);
                first = false;
            }
            query = query.CloseSubclause();
        }
        if (range is not null)
        {
            query = query.AndAlso().WhereEquals(nameof(ShopCatalogEntry.Currency), currency);
            if (range.FromMinor is { } from)
                query = query.AndAlso().WhereGreaterThanOrEqual(PriceField, from);
            if (range.ToMinor is { } to)
                query = query.AndAlso().WhereLessThan(PriceField, to);
        }
        foreach (var word in words)
        {
            query = query.AndAlso().OpenSubclause().Search(Text, $"*{word}*").Boost(2);
            if (typo && Similarity(word.Length) is { } similarity)
                query = query.OrElse().WhereEquals(Text, word).Fuzzy(similarity);
            query = query.CloseSubclause();
        }
        return query;
    }

    public static IAsyncDocumentQuery<ShopProduct> Order(IAsyncDocumentQuery<ShopProduct> query, string? sort, bool searching) => sort switch
    {
        "price-asc" => query.OrderBy(PriceField),
        "price-desc" => query.OrderByDescending(PriceField),
        "rating" => query.OrderByDescending(nameof(ShopCatalogEntry.Rating)),
        "newest" => query.OrderByDescending(nameof(ShopCatalogEntry.PublishedAt)),
        _ when searching => query.OrderByScore().OrderByDescending(nameof(ShopCatalogEntry.PublishedAt)),
        _ => query.OrderByDescending(nameof(ShopCatalogEntry.PublishedAt)),
    };

    public static List<SparkShopFacet> Facets(Dictionary<string, FacetResult> results, IReadOnlyList<string> selected)
    {
        if (!results.TryGetValue(FacetsField, out var result))
            return [];
        return result.Values
            .Where(v => v.Count > 0 && IsFacetTerm(v.Range))
            .GroupBy(v => v.Range[..v.Range.IndexOf(':')], StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new SparkShopFacet(g.Key, g
                .OrderByDescending(v => v.Count).ThenBy(v => v.Range, StringComparer.Ordinal)
                .Select(v => new SparkShopFacetValue(v.Range, v.Range[(v.Range.IndexOf(':') + 1)..], v.Count, selected.Contains(v.Range)))
                .ToList()))
            .ToList();
    }

    public static List<SparkShopPriceRange> PriceRanges(Dictionary<string, FacetResult> results, IReadOnlyList<CatalogPriceRange> ranges, int? selected)
    {
        var counts = results.Values.FirstOrDefault(r => r.Name != FacetsField)?.Values ?? [];
        return ranges.Select((r, i) => new SparkShopPriceRange(
                i,
                r.FromMinor is { } from ? Money.FromMinorUnits(from, r.Currency) : null,
                r.ToMinor is { } to ? Money.FromMinorUnits(to, r.Currency) : null,
                i < counts.Count ? counts[i].Count : 0,
                selected == i))
            .ToList();
    }
}
