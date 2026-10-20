using MintPlayer.Spark.Commerce.Checkout.Models;

namespace MintPlayer.Spark.Commerce.Checkout.Services;

/// <summary>
/// The order state machine of §4.4, for all three allocation modes (D1) and back-office orders (GB7). Pure: every
/// transition checkout makes goes through <see cref="CanMove"/>, so an illegal one (a late webhook moving a refunded
/// order back to Confirmed) is refused instead of stored.
/// </summary>
/// <remarks>
/// <code>
/// PaymentPending ─▶ Confirmed | Cancelled | Expired | Voided | Refunding (late payment lost, VoidOrder after capture)
/// Expired/Cancelled ─▶ Confirmed (a late payment that re-claimed, D3) | Refunding (it could not)
/// Confirmed ─▶ PartiallyRefunded | Refunding | Refunded | Disputed
/// PartiallyRefunded ─▶ Refunding | Refunded | Disputed
/// Refunding ─▶ Refunded | PartiallyRefunded (a refund failed for lack of balance, D20) | Disputed
/// Disputed ─▶ DisputeWon | DisputeLost
/// DisputeWon ─▶ Disputed (a new dispute) | Refunding | PartiallyRefunded | Refunded
/// </code>
/// CancellationRequested, WithdrawalRequested and GuaranteeClaim (M9) refine Confirmed through refunds; TicketsIssued,
/// FulfilmentRequested and Invoiced are order events heard by their packages, not states.
/// </remarks>
public static class OrderStateMachine
{
    private static readonly Dictionary<OrderState, OrderState[]> Allowed = new()
    {
        [OrderState.PaymentPending] = [OrderState.Confirmed, OrderState.Cancelled, OrderState.Expired, OrderState.Voided, OrderState.Refunding],
        [OrderState.Cancelled] = [OrderState.Confirmed, OrderState.Refunding],
        [OrderState.Expired] = [OrderState.Confirmed, OrderState.Refunding],
        [OrderState.Confirmed] = [OrderState.PartiallyRefunded, OrderState.Refunding, OrderState.Refunded, OrderState.Disputed],
        [OrderState.PartiallyRefunded] = [OrderState.Refunding, OrderState.Refunded, OrderState.Disputed],
        [OrderState.Refunding] = [OrderState.Refunded, OrderState.PartiallyRefunded, OrderState.Disputed],
        [OrderState.Refunded] = [OrderState.Disputed],
        [OrderState.Voided] = [],
        [OrderState.Disputed] = [OrderState.DisputeWon, OrderState.DisputeLost],
        [OrderState.DisputeWon] = [OrderState.Disputed, OrderState.Refunding, OrderState.PartiallyRefunded, OrderState.Refunded],
        [OrderState.DisputeLost] = [],
    };

    /// <summary>Whether <paramref name="from"/> may move to <paramref name="to"/>. Staying put is always allowed (idempotent webhooks).</summary>
    public static bool CanMove(OrderState from, OrderState to)
        => from == to || (Allowed.TryGetValue(from, out var targets) && targets.Contains(to));

    /// <summary>The states in which the order's units are sold (converted claims).</summary>
    public static bool IsSold(OrderState state)
        => state is OrderState.Confirmed or OrderState.PartiallyRefunded or OrderState.Disputed or OrderState.DisputeWon;

    /// <summary>The states that end the order for good.</summary>
    public static bool IsFinal(OrderState state)
        => state is OrderState.Voided or OrderState.DisputeLost;

    /// <summary>Moves <paramref name="order"/>, logging the transition; throws on an illegal one.</summary>
    public static OrderState Move(CheckoutOrder order, OrderState to, DateTimeOffset at, string? detail = null, string? actorId = null)
    {
        var from = order.State;
        if (!CanMove(from, to))
            throw new InvalidOperationException($"Order {order.Id} cannot move from {from} to {to}.");
        if (from != to)
        {
            order.State = to;
            order.Log.Add(new CheckoutOrderLogEntry { At = at, Event = "state:" + to, Detail = detail ?? ("from " + from), ActorId = actorId });
        }
        order.UpdatedAt = at;
        return from;
    }

    /// <summary>Moves when allowed; answers whether it did.</summary>
    public static bool TryMove(CheckoutOrder order, OrderState to, DateTimeOffset at, string? detail = null, string? actorId = null)
    {
        if (!CanMove(order.State, to))
            return false;
        Move(order, to, at, detail, actorId);
        return true;
    }
}
