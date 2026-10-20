namespace MintPlayer.Spark.Warehouse;

/// <summary>Received stock that needs a home (PRD §4.7, Receiving and put-away).</summary>
/// <param name="ReceiptId">The <c>InboundReceipt</c> or return receipt the stock came in on, or null for a manual move.</param>
/// <param name="Quantity">The quantity to put away, in base units.</param>
/// <param name="From">Where the stock is now, usually a Receiving location.</param>
public sealed record PutawayRequest(
    string SiteId,
    string OwnerId,
    string ItemId,
    decimal Quantity,
    string? LotCode,
    DateOnly? Expiry,
    WarehouseLocationRef? From,
    string? ReceiptId = null);

/// <summary>A location suggested for a put-away task.</summary>
/// <param name="Quantity">How much of the request fits there, or null for all of it. The engine asks the next rule for the rest.</param>
/// <param name="Reason">A short, untranslated reason code shown to supervisors, e.g. <c>fixed</c>, <c>same-lot</c>, <c>nearest-empty</c>.</param>
public sealed record PutawaySuggestion(WarehouseLocationRef Location, decimal? Quantity, string Reason);

/// <summary>
/// Suggests where received stock goes (PRD §4.7, Receiving and put-away). Rules run in <see cref="Order"/>; the first
/// that answers wins. The built-ins are, in order: the item's <b>fixed</b> location (100), a location already holding
/// the <b>same lot</b> (200), the <b>nearest empty</b> location by walk sequence (300). A custom rule picks an order
/// between or before them.
/// </summary>
/// <remarks>The put-away task confirms by scanning the suggested location; the suggestion itself writes nothing.</remarks>
public interface IPutawayRule
{
    /// <summary>The position in the chain; lower runs first.</summary>
    int Order { get; }

    /// <summary>A location for (part of) the stock, or null to let the next rule decide.</summary>
    ValueTask<PutawaySuggestion?> SuggestAsync(PutawayRequest request, CancellationToken cancellationToken = default);
}
