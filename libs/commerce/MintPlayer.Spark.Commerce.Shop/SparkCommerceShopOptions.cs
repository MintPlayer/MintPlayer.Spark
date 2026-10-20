using MintPlayer.Spark.Abstractions.Builder;

namespace MintPlayer.Spark.Commerce.Shop;

/// <summary>
/// <c>Spark:Commerce:Shop</c> (PRD §4.10). Code given to <c>AddCommerceShop(o => …)</c> runs after the section. Per-sale
/// values (refund policy) are chosen per product within the channel's bounds (<c>Spark:Commerce:Channels:{name}</c>, D2).
/// </summary>
public sealed class SparkCommerceShopOptions : ISparkConfigurationSection
{
    public static string SectionName => "Spark:Commerce:Shop";

    /// <summary>The channel a new product sells in when it names none. Default <c>shop</c>; must be configured in <c>Spark:Commerce:Channels</c>.</summary>
    public string DefaultChannel { get; set; } = "shop";

    /// <summary>The SPA route of a public product page, with <c>{slug}</c>. Used by the sitemap, the meta tags, the JSON-LD offers and the feeds. Default <c>/shop/products/{slug}</c>.</summary>
    public string ProductPagePath { get; set; } = "/shop/products/{slug}";

    /// <summary>The SPA route of a category page, with <c>{slug}</c>. Default <c>/shop/categories/{slug}</c>.</summary>
    public string CategoryPagePath { get; set; } = "/shop/categories/{slug}";

    /// <summary>Whether published products are listed in <c>/sitemap.xml</c> (X22) and get server-rendered meta tags (X6). Default true.</summary>
    public bool PublicPages { get; set; } = true;

    /// <summary>Who ships the goods (D13). Read at every decision, so a host can switch once the warehouse runs.</summary>
    public ShopFulfilmentMode Fulfilment { get; set; } = ShopFulfilmentMode.ShopOnly;

    /// <summary>Shards of a variant's quota at most (one per 250 units, at least one). Default 4.</summary>
    public int ShardsPerQuota { get; set; } = 4;

    /// <summary>At or below this many units left, a variant shows "only a few left" (schema.org <c>LimitedAvailability</c>). Default 5.</summary>
    public int LowStockThreshold { get; set; } = 5;

    /// <summary>The legal guarantee period shown in the notice (GA8; Directive (EU) 2019/771 Art. 10(1): at least two years). Default 24.</summary>
    public int LegalGuaranteeMonths { get; set; } = 24;

    /// <summary>
    /// GA8: the legal-guarantee notice per language (<c>{ "en": "…", "nl": "…" }</c>) shown on product pages and attached
    /// to the order confirmation; null takes the library's text. Replace it with the Commission's harmonised notice
    /// (CRD Art. 6(1)(l) and 22a as amended by Directive (EU) 2024/825; published as Implementing Regulation (EU) 2025/1960,
    /// applying from 27 September 2026; checked against EUR-Lex 2026-10-20).
    /// </summary>
    public Dictionary<string, string>? GuaranteeNotice { get; set; }

    /// <summary>The checkout delivery step.</summary>
    public ShopDeliveryOptions Delivery { get; set; } = new();

    /// <summary>Returns (GA1, GA8, GB5).</summary>
    public ShopReturnOptions Returns { get; set; } = new();

    /// <summary>Reviews (GC5).</summary>
    public ShopReviewOptions Reviews { get; set; } = new();

    /// <summary>Product feeds (GC6).</summary>
    public ShopFeedOptions Feeds { get; set; } = new();

    /// <summary>Catalogue facets (GB13).</summary>
    public ShopFacetOptions Facets { get; set; } = new();
}

/// <summary>Who ships the shop's goods (D13).</summary>
public enum ShopFulfilmentMode
{
    /// <summary>The shop books its shipments through Shipping (labels, tracking, return labels, multi-parcel, GB5).</summary>
    ShopOnly,
    /// <summary>
    /// A warehouse (Commerce.Fulfilment over Fulfilment.Contracts) ships: Shop never books a shipment or a return label,
    /// and stock, shipping, delivery and receipts reach it through <see cref="ISparkShopOrders"/>,
    /// <see cref="ISparkShopStock"/> and Checkout's <c>ISparkCheckoutRefunds</c>.
    /// </summary>
    Warehouse,
}

/// <summary>The checkout delivery step (PRD §4.1: "the checkout shipping step lives in Shop").</summary>
public sealed class ShopDeliveryOptions
{
    /// <summary>The pricing step's order. Default 500: after Promotions (50, 100), before the donation round-up (900) and insurance (950).</summary>
    public int PricingStepOrder { get; set; } = 500;

    /// <summary>How long a buyer's delivery choice is kept without being used. Default 7 days.</summary>
    public TimeSpan ChoiceLifetime { get; set; } = TimeSpan.FromDays(7);

    /// <summary>The weight assumed for a variant without one, in grams. Default 500.</summary>
    public int DefaultWeightGrams { get; set; } = 500;
}

/// <summary>Returns (GA1, GA8, GB5).</summary>
public sealed class ShopReturnOptions
{
    /// <summary>
    /// ShopOnly: a return label is booked and mailed to the buyer as soon as goods are expected back (a withdrawal, a
    /// guarantee claim, a seller-allowed return). Default true.
    /// </summary>
    public bool AutoReturnLabel { get; set; } = true;

    /// <summary>
    /// When the goods count as received (CRD Art. 13(3)). <see cref="ShopReceiptMoment.CarrierDelivery"/> (default): the
    /// carrier's delivery of the return shipment registers the receipt, so a withheld refund is paid without waiting for
    /// staff; diminished value (Art. 14(2)) can then only be deducted when dispatch was not proven first.
    /// <see cref="ShopReceiptMoment.StaffInspection"/>: staff register the receipt at inspection, with any deduction.
    /// </summary>
    public ShopReceiptMoment ReceiptOn { get; set; } = ShopReceiptMoment.CarrierDelivery;

    /// <summary>The MailManager template of the return-label mail. Default <c>SparkShop/ReturnLabel</c>.</summary>
    public string ReturnLabelMailTemplate { get; set; } = "SparkShop/ReturnLabel";
}

/// <summary>When returned goods count as received.</summary>
public enum ShopReceiptMoment
{
    CarrierDelivery,
    StaffInspection,
}

/// <summary>Reviews (GC5).</summary>
public sealed class ShopReviewOptions
{
    /// <summary>Whether buyers may review (a product may still switch its own reviews off). Default true.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Whether a review waits for a moderator (<c>Review/Moderation</c>) before it is shown. Default true.</summary>
    public bool PreModeration { get; set; } = true;

    /// <summary>How long after delivery a line may be reviewed. Default 365 days.</summary>
    public TimeSpan Window { get; set; } = TimeSpan.FromDays(365);

    /// <summary>The longest review text. Default 4000 characters.</summary>
    public int MaxLength { get; set; } = 4000;
}

/// <summary>Product feeds (GC6).</summary>
public sealed class ShopFeedOptions
{
    /// <summary>Whether <c>/spark/commerce/shop/feeds/*</c> answer. Default true.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How long a generated feed is served from the cache. Default 1 hour.</summary>
    public TimeSpan CacheDuration { get; set; } = TimeSpan.FromHours(1);

    /// <summary>The most items one feed lists. Default 50,000.</summary>
    public int MaxItems { get; set; } = 50_000;
}

/// <summary>Catalogue facets (GB13).</summary>
public sealed class ShopFacetOptions
{
    /// <summary>
    /// The price-range boundaries of the price facet, in major units of the product's currency; null or empty takes 10, 25,
    /// 50, 100, 250. (Null by default: configuration binding appends to a pre-filled list.)
    /// </summary>
    public List<decimal>? PriceRanges { get; set; }

    internal IReadOnlyList<decimal> EffectivePriceRanges => PriceRanges is { Count: > 0 } ranges
        ? ranges.Where(r => r > 0).Distinct().Order().ToList()
        : [10m, 25m, 50m, 100m, 250m];

    /// <summary>The most catalogue results one page returns. Default 48.</summary>
    public int MaxPageSize { get; set; } = 48;
}
