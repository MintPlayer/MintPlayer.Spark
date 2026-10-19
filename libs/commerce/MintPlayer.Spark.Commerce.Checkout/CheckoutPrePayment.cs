using MintPlayer.Spark.Commerce.Checkout.Models;

namespace MintPlayer.Spark.Commerce.Checkout;

/// <summary>
/// A check that runs when "Pay" is pressed, after the order's lines are built and claimed and before the payment is
/// created (or a zero-total order confirms). Throw a <see cref="CheckoutException"/> to refuse: the placement's claims
/// are released and the buyer sees the refusal. Ticketing.Events registers one for required attendee questions (GB3);
/// a host may register its own. Runs in <see cref="Order"/> order; must not call providers or mail.
/// </summary>
public interface ICheckoutPrePaymentCheck
{
    int Order => 0;

    Task CheckAsync(CheckoutPrePaymentContext context, CancellationToken cancellationToken);
}

/// <summary>What a <see cref="ICheckoutPrePaymentCheck"/> sees, and the documents it saves with the order.</summary>
public sealed class CheckoutPrePaymentContext
{
    /// <summary>The order about to be stored: its id, lines (with claims) and buyer are set; it is not saved yet.</summary>
    public required CheckoutOrder Order { get; init; }

    /// <summary>
    /// The data the client sent with "Pay" (<c>SparkPlaceOrderRequest.Data</c>), by key. Each check owns its keys
    /// (Ticketing.Events: <c>attendeeAnswers</c>). Values are JSON or plain text, as the check documents.
    /// </summary>
    public required IReadOnlyDictionary<string, string> Data { get; init; }

    /// <summary>The buyer's language (for consent texts shown), when the client sent one.</summary>
    public string? Language { get; init; }

    internal List<(object Document, string Id)> Documents { get; } = [];

    /// <summary>
    /// Stores <paramref name="document"/> in the same session and transaction as the order: it exists exactly when the
    /// order does (a refused or failed placement stores neither).
    /// </summary>
    public void StoreWithOrder(object document, string id)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        Documents.Add((document, id));
    }
}
