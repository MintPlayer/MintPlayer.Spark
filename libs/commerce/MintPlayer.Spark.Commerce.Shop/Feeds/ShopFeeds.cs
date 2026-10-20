using System.Globalization;
using System.Text;
using System.Xml;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Commerce.Shop.Catalog;
using MintPlayer.Spark.Commerce.Shop.Models;
using MintPlayer.Spark.Commerce.Shop.Services;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Commerce.Shop.Feeds;

/// <summary>The product feeds (GC6).</summary>
public enum ShopFeedKind
{
    /// <summary>Google Merchant Center: RSS 2.0 with the <c>g:</c> namespace (<c>/spark/commerce/shop/feeds/google?seller=</c>).</summary>
    Google,
    /// <summary>Meta (Facebook/Instagram) catalogue: CSV (<c>/spark/commerce/shop/feeds/meta?seller=</c>).</summary>
    Meta,
}

/// <summary>A generated feed: the bytes and their media type.</summary>
public sealed record ShopFeed(string ContentType, string FileName, byte[] Content);

/// <summary>
/// The product feeds of one seller (GC6, PRD §4.10): every active variant of the seller's published products, with its
/// FinalPrice (§4.4), availability, GTIN and images. Served at a stable URL per seller and kind and cached for
/// <c>Feeds:CacheDuration</c>. Written against the public specifications (Google Merchant Center "Product data
/// specification"; Meta "Catalog: supported fields for products"); neither platform is called.
/// </summary>
public interface ISparkShopFeeds
{
    /// <summary>The feed of <paramref name="sellerId"/> (null or <c>platform</c>: the platform's own products).</summary>
    Task<ShopFeed> GetAsync(ShopFeedKind kind, string? sellerId, string? publicOrigin, CancellationToken cancellationToken = default);
}

/// <summary>One row of a feed: a variant as both platforms list it.</summary>
internal sealed record ShopFeedItem(
    string Id,
    string GroupId,
    string Title,
    string Description,
    string Link,
    string? ImageLink,
    IReadOnlyList<string> AdditionalImages,
    Money Price,
    ShopAvailability Availability,
    string? Brand,
    string? Gtin,
    string? Mpn,
    string? Color,
    string? Size,
    Money? UnitPrice,
    string? UnitPricingMeasure,
    string? UnitPricingBaseMeasure,
    int? MinimumAge);

internal sealed class ShopFeeds(
    IDocumentStore store,
    SparkShopCatalog catalog,
    IMemoryCache cache,
    IOptionsMonitor<SparkCommerceShopOptions> options) : ISparkShopFeeds
{
    public async Task<ShopFeed> GetAsync(ShopFeedKind kind, string? sellerId, string? publicOrigin, CancellationToken cancellationToken = default)
    {
        var seller = string.IsNullOrWhiteSpace(sellerId) || sellerId == "platform" ? null : sellerId;
        var origin = (publicOrigin ?? "").TrimEnd('/');
        var key = $"spark-shop-feed:{kind}:{seller ?? "platform"}:{origin}";
        if (cache.TryGetValue(key, out ShopFeed? cached) && cached is not null)
            return cached;
        var items = await ItemsAsync(seller, origin, cancellationToken);
        var feed = kind == ShopFeedKind.Google
            ? new ShopFeed("application/rss+xml; charset=utf-8", "products.xml", Encoding.UTF8.GetBytes(GoogleXml(items, origin)))
            : new ShopFeed("text/csv; charset=utf-8", "products.csv", Encoding.UTF8.GetBytes(MetaCsv(items)));
        cache.Set(key, feed, options.CurrentValue.Feeds.CacheDuration);
        return feed;
    }

    private async Task<List<ShopFeedItem>> ItemsAsync(string? seller, string origin, CancellationToken cancellationToken)
    {
        var settings = options.CurrentValue;
        using var session = store.OpenAsyncSession();
        var products = await session.Query<ShopProduct>()
            .Where(p => p.IsPublished == true && p.OrganizationId == seller)
            .OrderBy(p => p.Id)
            .Take(settings.Feeds.MaxItems)
            .ToListAsync(cancellationToken);
        var items = new List<ShopFeedItem>();
        foreach (var product in products)
        {
            var variants = (await ShopPublisher.VariantsAsync(session, product.Id, cancellationToken)).Where(v => v.IsActive).ToList();
            foreach (var variant in variants)
            {
                if (items.Count >= settings.Feeds.MaxItems)
                    return items;
                if (ProductRules.PriceOf(product, variant) is not { } price)
                    continue;
                var final = catalog.FinalUnit(product, price);
                var (availability, _) = await catalog.AvailabilityAsync(variant, cancellationToken);
                items.Add(Item(product, variant, final, availability, origin, settings));
            }
        }
        return items;
    }

    /// <summary>A feed row of a variant. Pure.</summary>
    internal static ShopFeedItem Item(ShopProduct product, ShopVariant variant, Money finalPrice, ShopAvailability availability, string origin, SparkCommerceShopOptions settings)
    {
        var language = TranslatedString.DefaultLanguage;
        var title = ProductRules.Title(product, variant).GetValue(language) ?? variant.Sku;
        var description = product.Description?.GetValue(language) is { Length: > 0 } d ? d : title;
        var images = product.Media.Where(m => m.VariantIds.Count == 0 || m.VariantIds.Contains(variant.Id!))
            .OrderBy(m => m.VariantIds.Count == 0 ? 1 : 0).ThenBy(m => m.SortOrder)
            .Select(m => Absolute(origin, m.Url)).ToList();
        var link = Absolute(origin, settings.ProductPagePath.Replace("{slug}", Uri.EscapeDataString(product.Slug), StringComparison.Ordinal))
            + "?variant=" + Uri.EscapeDataString(variant.Id!);
        var net = ProductRules.NetQuantityOf(product, variant);
        var unitPrice = ProductRules.UnitPrice(finalPrice, product.UnitOfMeasure, net, product.UnitPriceReference);
        return new ShopFeedItem(
            Id: variant.Sku,
            GroupId: product.Id!,
            Title: Truncate(title, 150),
            Description: Truncate(description, 5000),
            Link: link,
            ImageLink: images.FirstOrDefault(),
            AdditionalImages: images.Skip(1).Take(10).ToList(),
            Price: finalPrice,
            Availability: availability,
            Brand: product.Brand,
            Gtin: variant.Gtin,
            Mpn: product.Mpn,
            Color: Option(variant, "colour", "color"),
            Size: Option(variant, "size"),
            UnitPrice: unitPrice,
            UnitPricingMeasure: unitPrice is null || net is null ? null : Measure(net.Value, product.UnitOfMeasure),
            UnitPricingBaseMeasure: unitPrice is null ? null : Measure(product.UnitPriceReference is > 0 and var r ? r : 1m, product.UnitOfMeasure),
            MinimumAge: product.MinimumAge is > 0 ? product.MinimumAge : null);
    }

    private static string? Option(ShopVariant variant, params string[] names)
        => variant.Options.FirstOrDefault(o => names.Contains(o.Name, StringComparer.Ordinal))?.Value;

    /// <summary>Google's unit-pricing measure (<c>750 ml</c> is written as <c>0.75 l</c>; Google accepts both).</summary>
    private static string? Measure(decimal quantity, EUnitOfMeasure unit) => unit switch
    {
        EUnitOfMeasure.Kilogram => Number(quantity) + " kg",
        EUnitOfMeasure.Litre => Number(quantity) + " l",
        EUnitOfMeasure.Metre => Number(quantity) + " m",
        EUnitOfMeasure.SquareMetre => Number(quantity) + " sqm",
        EUnitOfMeasure.CubicMetre => Number(quantity) + " cbm",
        EUnitOfMeasure.Piece => Number(quantity) + " ct",
        _ => null,
    };

    private static string Number(decimal value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>A price as both platforms take it: <c>15.00 EUR</c> (the currency's minor-unit digits, invariant culture).</summary>
    internal static string Price(Money money)
        => money.Amount.ToString("F" + Money.MinorUnitDigits(money.Currency), CultureInfo.InvariantCulture) + " " + money.Currency;

    internal static string Absolute(string origin, string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var absolute) && (absolute.Scheme == Uri.UriSchemeHttps || absolute.Scheme == Uri.UriSchemeHttp)
            ? url
            : origin + (url.StartsWith('/') ? url : "/" + url);

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..(max - 1)].TrimEnd() + "…";

    // ---- Google Merchant Center ------------------------------------------------------------------------------------

    private const string GoogleNamespace = "http://base.google.com/ns/1.0";

    /// <summary>
    /// Google's availability values (<c>in_stock</c>, <c>out_of_stock</c>, <c>preorder</c>, <c>backorder</c>): a few
    /// left is still <c>in_stock</c>.
    /// </summary>
    internal static string GoogleAvailability(ShopAvailability availability)
        => availability == ShopAvailability.OutOfStock ? "out_of_stock" : "in_stock";

    /// <summary>The RSS 2.0 feed with Google's <c>g:</c> attributes (one <c>item</c> per variant, grouped by <c>g:item_group_id</c>). Pure.</summary>
    internal static string GoogleXml(IReadOnlyList<ShopFeedItem> items, string origin)
    {
        var builder = new StringBuilder();
        using (var xml = XmlWriter.Create(builder, new XmlWriterSettings { Indent = true, Encoding = Encoding.UTF8, OmitXmlDeclaration = false }))
        {
            xml.WriteStartDocument();
            xml.WriteStartElement("rss");
            xml.WriteAttributeString("version", "2.0");
            xml.WriteAttributeString("xmlns", "g", null, GoogleNamespace);
            xml.WriteStartElement("channel");
            xml.WriteElementString("title", "Products");
            xml.WriteElementString("link", origin.Length == 0 ? "/" : origin);
            xml.WriteElementString("description", "Product feed");
            foreach (var item in items)
            {
                xml.WriteStartElement("item");
                G(xml, "id", item.Id);
                G(xml, "item_group_id", item.GroupId);
                G(xml, "title", item.Title);
                G(xml, "description", item.Description);
                G(xml, "link", item.Link);
                G(xml, "image_link", item.ImageLink);
                foreach (var image in item.AdditionalImages)
                    G(xml, "additional_image_link", image);
                G(xml, "availability", GoogleAvailability(item.Availability));
                G(xml, "price", Price(item.Price));
                G(xml, "condition", "new");
                G(xml, "brand", item.Brand);
                G(xml, "gtin", item.Gtin);
                G(xml, "mpn", item.Mpn);
                if (item.Gtin is null && (item.Brand is null || item.Mpn is null))
                    G(xml, "identifier_exists", "no");
                G(xml, "color", item.Color);
                G(xml, "size", item.Size);
                G(xml, "unit_pricing_measure", item.UnitPricingMeasure);
                G(xml, "unit_pricing_base_measure", item.UnitPricingBaseMeasure);
                if (item.MinimumAge is >= 18)
                    G(xml, "adult", "yes");
                xml.WriteEndElement();
            }
            xml.WriteEndElement();
            xml.WriteEndElement();
            xml.WriteEndDocument();
        }
        // StringBuilder output declares utf-16; the bytes are UTF-8.
        return builder.ToString().Replace("encoding=\"utf-16\"", "encoding=\"utf-8\"", StringComparison.Ordinal);
    }

    private static void G(XmlWriter xml, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            xml.WriteElementString("g", name, GoogleNamespace, value);
    }

    // ---- Meta --------------------------------------------------------------------------------------------------------

    internal static readonly string[] MetaColumns =
        ["id", "item_group_id", "title", "description", "availability", "condition", "price", "link", "image_link", "additional_image_link", "brand", "gtin", "mpn", "color", "size"];

    /// <summary>Meta's availability values (<c>in stock</c>, <c>out of stock</c>).</summary>
    internal static string MetaAvailability(ShopAvailability availability)
        => availability == ShopAvailability.OutOfStock ? "out of stock" : "in stock";

    /// <summary>The CSV feed (RFC 4180 quoting; a cell starting with <c>= + - @</c> is prefixed with <c>'</c> against spreadsheet formula injection). Pure.</summary>
    internal static string MetaCsv(IReadOnlyList<ShopFeedItem> items)
    {
        var builder = new StringBuilder();
        builder.AppendJoin(',', MetaColumns).Append("\r\n");
        foreach (var item in items)
        {
            string?[] cells =
            [
                item.Id, item.GroupId, item.Title, item.Description, MetaAvailability(item.Availability), "new", Price(item.Price),
                item.Link, item.ImageLink, string.Join(",", item.AdditionalImages), item.Brand, item.Gtin, item.Mpn, item.Color, item.Size,
            ];
            builder.AppendJoin(',', cells.Select(Cell)).Append("\r\n");
        }
        return builder.ToString();
    }

    internal static string Cell(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return "";
        if (value[0] is '=' or '+' or '-' or '@')
            value = "'" + value;
        return value.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : value;
    }
}
