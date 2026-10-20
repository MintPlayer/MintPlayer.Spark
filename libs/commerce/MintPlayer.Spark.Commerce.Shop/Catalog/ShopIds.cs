namespace MintPlayer.Spark.Commerce.Shop.Catalog;

/// <summary>
/// The checkout item ids of the shop: <c>shop:{variantId}</c>. One prefix, so the capacity source, the refund terms and
/// the order-event recipient recognise the shop's lines without loading anything.
/// </summary>
public static class ShopItemIds
{
    public const string Prefix = "shop:";

    public static string For(string variantId) => Prefix + variantId;

    public static bool Handles(string? itemId) => itemId is { Length: > 5 } && itemId.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>The variant id of a shop item id, or null for any other item.</summary>
    public static string? VariantId(string? itemId) => Handles(itemId) ? itemId![Prefix.Length..] : null;
}

/// <summary>The inventory quotas of the shop (PRD §4.3): one per variant that tracks its inventory.</summary>
public static class ShopQuotas
{
    public static string Variant(string variantId) => $"shop-quotas/{variantId}";

    /// <summary>The shard count of a quota: one per 250 units, at least one, at most <paramref name="max"/>.</summary>
    public static int Shards(int capacity, int max) => Math.Clamp(capacity / 250, 1, Math.Max(1, max));
}

/// <summary>The ids of the shop's own documents that are derived from another id (X3: atomic natural-id create).</summary>
public static class ShopDocumentIds
{
    /// <summary>The shop's side of an order for one seller (<c>null</c>: the platform).</summary>
    public static string Order(string orderId, string? sellerOrganizationId) => $"ShopOrders/{orderId}/{sellerOrganizationId ?? "platform"}";

    /// <summary>The one review of an order line.</summary>
    public static string Review(string orderId, string lineId) => $"ShopProductReviews/{orderId}/{lineId}";
}

/// <summary>The basket choices the shop's delivery step reads and writes (Checkout's free-form <c>Choices</c>).</summary>
public static class ShopChoices
{
    /// <summary>The id of the buyer's <see cref="Models.ShopDeliveryChoice"/>.</summary>
    public const string Delivery = "shop.delivery";

    /// <summary>The order-extra code prefix of the delivery (<c>delivery:{method}</c>): Checkout's default delivery recognition (GA1) matches it too.</summary>
    public const string DeliveryExtraPrefix = "delivery:";

    public const string Source = "commerce-shop";
}
