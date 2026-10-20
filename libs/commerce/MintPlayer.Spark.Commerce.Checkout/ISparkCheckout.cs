using MintPlayer.Spark.Commerce.Checkout.Contracts;
using MintPlayer.Spark.Commerce.Checkout.Models;

namespace MintPlayer.Spark.Commerce.Checkout;

/// <summary>
/// Checkout's facade for the current request's buyer (§4.4): the basket, the last step (GA2) and placing the order.
/// Registered scoped by <c>AddCommerceCheckout()</c>; the endpoints under <c>/spark/commerce/checkout</c> call it.
/// </summary>
public interface ISparkCheckout
{
    Task<SparkBasket> GetBasketAsync(CancellationToken cancellationToken = default);

    /// <summary>Adds a line. In a HoldFromSelection channel the units are held now (D1); a gated item needs its admission token (§4.12).</summary>
    /// <exception cref="CheckoutException">Refused (<see cref="CheckoutRefusals"/>).</exception>
    Task<SparkBasket> AddLineAsync(SparkBasketAddRequest request, CancellationToken cancellationToken = default);

    /// <summary>Changes a line's quantity (0 removes it, with its add-ons); a held line's claim is adjusted, all or nothing.</summary>
    Task<SparkBasket> SetQuantityAsync(string lineId, int quantity, CancellationToken cancellationToken = default);

    /// <summary>
    /// GA10: "More time". Every live hold moves on by its channel's <c>Hold:Extension</c>, capped at <c>Hold:Max</c> from
    /// when it was taken; refused with <see cref="CheckoutRefusals.HoldNotExtendable"/> when none can move.
    /// </summary>
    Task<SparkBasket> ExtendHoldAsync(CancellationToken cancellationToken = default);

    /// <summary>Sets the pricing steps' choices (donation, insurance, codes) and a guest's e-mail address.</summary>
    Task<SparkBasket> SetChoicesAsync(IReadOnlyDictionary<string, string?> choices, string? email, CancellationToken cancellationToken = default);

    /// <summary>The last step (GA2): pre-contract information, documents to accept, methods, offers.</summary>
    Task<SparkCheckoutSummary> GetSummaryAsync(CancellationToken cancellationToken = default);

    /// <summary>"Order with obligation to pay": claims (per mode), the payment, the acceptances; zero totals confirm at once (D6).</summary>
    Task<SparkPlaceOrderResult> PlaceOrderAsync(SparkPlaceOrderRequest request, CancellationToken cancellationToken = default);

    /// <summary>The buyer's own order by number; null when it is not theirs.</summary>
    Task<CheckoutOrder?> GetOwnOrderAsync(string number, CancellationToken cancellationToken = default);

    /// <summary>The buyer abandons the payment: the provider payment is cancelled actively and the holds go back at once (D3).</summary>
    Task<CheckoutOrder?> CancelPaymentAsync(string number, CancellationToken cancellationToken = default);

    /// <summary>Shipping/Fulfilment mark a line shipped: a later refund returns no quota until restock (I4).</summary>
    Task MarkShippedAsync(string orderId, string lineId, DateTimeOffset shippedAt, CancellationToken cancellationToken = default);
}
