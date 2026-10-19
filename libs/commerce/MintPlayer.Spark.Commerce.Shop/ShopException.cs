namespace MintPlayer.Spark.Commerce.Shop;

/// <summary>
/// Why the shop refused something. Codes are permanent: the client translates <c>commerceShop.refusal.{code}</c>
/// (translations.json) and the endpoints answer them in <c>SparkShopRefusal</c> with HTTP 409.
/// </summary>
public static class ShopRefusals
{
    public const string ProductNotFound = "productNotFound";
    public const string OrderNotFound = "orderNotFound";
    public const string Forbidden = "forbidden";
    public const string InvalidAddress = "invalidAddress";
    public const string MethodNotOffered = "methodNotOffered";
    public const string ServicePointRequired = "servicePointRequired";
    public const string NothingToShip = "nothingToShip";
    public const string WarehouseShips = "warehouseShips";
    public const string ReturnNotFound = "returnNotFound";
    public const string ReviewsClosed = "reviewsClosed";
    public const string NotVerified = "notVerified";
    public const string AlreadyReviewed = "alreadyReviewed";
    public const string InvalidReview = "invalidReview";
    public const string ReasonRequired = "reasonRequired";

    /// <summary>
    /// The pricing step's refusal at "Order with obligation to pay" when the basket holds goods and no delivery was chosen.
    /// Raised as a <c>CheckoutException</c>, so the checkout page translates it as <c>commerceCheckout.refusal.deliveryRequired</c>.
    /// </summary>
    public const string DeliveryRequired = "deliveryRequired";
}

/// <summary>A shop refusal: a <see cref="ShopRefusals"/> code, an English message for logs, and what it concerns.</summary>
public sealed class ShopException(string code, string message, string? subject = null) : Exception(message)
{
    public string Code { get; } = code;

    public string? Subject { get; } = subject;

    public static ShopException Of(string code, string? subject = null, string? message = null)
        => new(code, message ?? $"Shop refused: {code}{(subject is null ? "" : " (" + subject + ")")}.", subject);
}
