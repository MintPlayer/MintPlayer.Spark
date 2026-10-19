using MintPlayer.Spark.Commerce.Checkout.Models;
using MintPlayer.Spark.Commerce.Inventory;

namespace MintPlayer.Spark.Commerce.Checkout.Services;

/// <summary>
/// The inventory side of checkout: one claim per line (so each line keeps its channel's rules, D40), claim requests
/// built from the stored capacity shape, and the engine's statuses mapped to refusals.
/// </summary>
internal static class OrderClaims
{
    /// <summary>The claim id of a basket or order line (stable, so every engine call is idempotent by it).</summary>
    public static string ClaimIdFor(string lineId) => "claims/checkout/" + lineId;

    /// <summary>The per-buyer limit lines of a line (D16): every identifier the buyer has, under the item's scope.</summary>
    public static IReadOnlyList<LimitLine> Limits(CheckoutItem item, CommerceChannelOptions channel, int quantity, BuyerIdentifiers buyer)
    {
        var max = item.PerBuyerLimit ?? channel.PerBuyerLimit;
        if (max is not { } m)
            return [];
        return InventoryLimitKeys.For(LimitScope(item), m, quantity, buyer);
    }

    public static string LimitScope(CheckoutItem item) => item.LimitScope ?? "channels/" + item.Channel;

    /// <summary>The claim request for a line's capacity shape.</summary>
    public static ClaimRequest Request(string claimId, string owner, DateTimeOffset expiresAt, CheckoutCapacitySnapshot capacity, IReadOnlyList<LimitLine> limits)
        => new(claimId, owner, expiresAt, capacity.Quotas,
            Units: capacity.Units.Count > 0 ? capacity.Units : null,
            Limits: limits.Count > 0 ? limits : null,
            Items: capacity.Items.Count > 0 ? capacity.Items : null,
            BestAvailable: capacity.BestAvailable.Count > 0 ? capacity.BestAvailable : null,
            Picks: capacity.Picks.Count > 0 ? capacity.Picks : null);

    /// <summary>
    /// The shape to re-claim the <b>same</b> units a lapsed claim held (late payment, D3): chain-filled seats become
    /// explicit units, so a re-claim never seats the buyer elsewhere.
    /// </summary>
    public static CheckoutCapacitySnapshot SameUnits(CheckoutCapacitySnapshot capacity, IReadOnlyList<string> heldUnits)
    {
        if (heldUnits.Count == 0)
            return capacity;
        return new CheckoutCapacitySnapshot
        {
            Items = capacity.Items,
            Quotas = capacity.Quotas,
            Units = heldUnits.ToList(),
            BestAvailable = [],
            Picks = [],
        };
    }

    /// <summary>The refusal a failed engine call means.</summary>
    public static CheckoutException Refusal(ClaimResult result, string? lineId) => result.Status switch
    {
        ClaimStatus.SoldOut => CheckoutException.Of(CheckoutRefusals.SoldOut, lineId, result.Subject),
        ClaimStatus.UnitTaken => CheckoutException.Of(CheckoutRefusals.UnitTaken, lineId, result.Subject),
        // Never name the hashed limit key to the client: it identifies the buyer's identifiers.
        ClaimStatus.LimitExceeded => CheckoutException.Of(CheckoutRefusals.LimitExceeded, lineId),
        ClaimStatus.NotTogether => CheckoutException.Of(CheckoutRefusals.NotTogether, lineId, result.Subject),
        ClaimStatus.LeavesSingleSeat => CheckoutException.Of(CheckoutRefusals.LeavesSingleSeat, lineId, result.Subject),
        ClaimStatus.Expired => CheckoutException.Of(CheckoutRefusals.HoldExpired, lineId),
        ClaimStatus.NotFound => CheckoutException.Of(CheckoutRefusals.ItemNotFound, lineId, result.Subject),
        _ => CheckoutException.Of(CheckoutRefusals.Conflict, lineId),
    };

    /// <summary>Whether a refused conversion was the card-fingerprint key (D29), as opposed to the line's own limits.</summary>
    public static bool IsCardLimit(ClaimResult result)
        => result.Status == ClaimStatus.LimitExceeded && result.Subject is { } subject && subject.Contains("/" + InventoryLimitKeys.Card + "/", StringComparison.Ordinal);
}
