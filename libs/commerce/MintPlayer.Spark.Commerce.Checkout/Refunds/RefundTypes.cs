using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Commerce.Inventory;

namespace MintPlayer.Spark.Commerce.Checkout.Refunds;

// ---- the return-reason taxonomy (GA8, PRD §4.7) -------------------------------------------------------------------

/// <summary>Why goods come back (GA8). The reason picks the flow, the refund rules and the warehouse disposition default (<see cref="ReturnReasons"/>).</summary>
public enum ReturnReasonCode
{
    /// <summary>The statutory withdrawal (GA1, CRD Art. 9–16): unconditional, no approval.</summary>
    Withdrawal,
    /// <summary>A lack of conformity under the legal guarantee (GA8, Directive (EU) 2019/771).</summary>
    NonConformity,
    /// <summary>The seller sent the wrong item.</summary>
    WrongItem,
    /// <summary>The goods arrived damaged (the risk passes on delivery, CRD Art. 20).</summary>
    DamagedInTransit,
    /// <summary>A goodwill return or cancellation the seller allows under its own policy (D20).</summary>
    SellerAllowed,
    Other,
}

/// <summary>What the shop or warehouse does with goods that came back.</summary>
public enum ReturnDisposition
{
    /// <summary>Back on sale: <see cref="ISparkInventory.RestockAsync"/> returns the quota units (I4).</summary>
    Restock,
    /// <summary>Checked first; restocked or quarantined after.</summary>
    Inspect,
    /// <summary>Kept apart (defective, damaged); never back on sale automatically.</summary>
    Quarantine,
    /// <summary>Written off.</summary>
    Dispose,
}

/// <summary>The rules each <see cref="ReturnReasonCode"/> carries (PRD §4.7 "Return reasons").</summary>
/// <param name="Flow">The flow the reason opens.</param>
/// <param name="RefundsFees">Whether service and platform fees are refunded regardless of the channel's <c>RefundFees</c>.</param>
/// <param name="Delivery">How the delivery cost is refunded.</param>
/// <param name="SellerPaysReturnShipping">Whether the seller bears the return shipping (null: the seller's withdrawal setting).</param>
/// <param name="DefaultDisposition">The warehouse disposition default.</param>
/// <param name="InventoryReason">The reason the inventory engine records.</param>
public sealed record ReturnReasonRules(ReturnFlow Flow, bool RefundsFees, DeliveryRefund Delivery, bool? SellerPaysReturnShipping, ReturnDisposition DefaultDisposition, ReturnReason InventoryReason);

/// <summary>The flows of PRD §4.10: the unconditional withdrawal, a guarantee claim, a seller-allowed (or seller-fault) return.</summary>
public enum ReturnFlow
{
    Withdrawal,
    GuaranteeClaim,
    SellerReturn,
}

/// <summary>How a refund treats the order's delivery extra.</summary>
public enum DeliveryRefund
{
    /// <summary>Never refunded.</summary>
    None,
    /// <summary>The standard (cheapest) delivery only; the surcharge for a more expensive method is kept (CRD Art. 13(2)).</summary>
    Standard,
    /// <summary>Everything the buyer paid for delivery (the seller's fault).</summary>
    Full,
}

/// <summary>The taxonomy's rules, one place (GA8).</summary>
public static class ReturnReasons
{
    public static ReturnReasonRules For(ReturnReasonCode reason) => reason switch
    {
        // CRD Art. 13(1): "all payments received from the consumer, including, if applicable, the costs of delivery";
        // Art. 13(2): not the supplement for a delivery other than the least expensive standard one.
        ReturnReasonCode.Withdrawal => new(ReturnFlow.Withdrawal, RefundsFees: true, DeliveryRefund.Standard, null, ReturnDisposition.Restock, ReturnReason.Withdrawal),
        // Directive (EU) 2019/771 Art. 14(1)(a), 14(2): free of charge, the seller takes the goods back at its expense.
        ReturnReasonCode.NonConformity => new(ReturnFlow.GuaranteeClaim, RefundsFees: true, DeliveryRefund.Full, true, ReturnDisposition.Inspect, ReturnReason.Refund),
        ReturnReasonCode.WrongItem => new(ReturnFlow.SellerReturn, RefundsFees: true, DeliveryRefund.Full, true, ReturnDisposition.Restock, ReturnReason.Refund),
        ReturnReasonCode.DamagedInTransit => new(ReturnFlow.SellerReturn, RefundsFees: true, DeliveryRefund.Full, true, ReturnDisposition.Quarantine, ReturnReason.Refund),
        ReturnReasonCode.SellerAllowed => new(ReturnFlow.SellerReturn, RefundsFees: false, DeliveryRefund.None, false, ReturnDisposition.Restock, ReturnReason.Refund),
        _ => new(ReturnFlow.SellerReturn, RefundsFees: false, DeliveryRefund.None, false, ReturnDisposition.Inspect, ReturnReason.Other),
    };
}

/// <summary>What a refund is for.</summary>
public enum RefundKind
{
    /// <summary>The buyer cancels within the seller's refund policy (D20): none, until X days before, or with a fee.</summary>
    Cancellation,
    /// <summary>The event was cancelled: a mass refund (D20).</summary>
    EventCancelled,
    /// <summary>The event was rescheduled and the buyer chose a refund within the window (D20).</summary>
    Rescheduled,
    /// <summary>The statutory withdrawal (GA1).</summary>
    Withdrawal,
    /// <summary>A guarantee remedy: price reduction or termination (GA8).</summary>
    Guarantee,
    /// <summary>Staff refund for a return reason (wrong item, damaged, goodwill).</summary>
    Return,
}

/// <summary>Where a refund stands.</summary>
public enum RefundStatus
{
    /// <summary>Asked of the provider; not paid out yet.</summary>
    Pending,
    /// <summary>Paid out (or nothing to pay: a zero amount). <c>RefundIssued</c> is raised.</summary>
    Succeeded,
    /// <summary>The provider refused it (insufficient balance, D20, or another reason). Finance retries or settles by hand.</summary>
    Failed,
    /// <summary>No provider payment to refund (a manual back-office order): finance must pay it back outside and settle.</summary>
    ManualRequired,
    /// <summary>Finance paid it back outside the provider (audited). <c>RefundIssued</c> is raised.</summary>
    SettledManually,
}

// ---- the seller refund policy and the item's withdrawal terms ------------------------------------------------------

/// <summary>A seller refund policy (D20) for buyer cancellations; never applies to the withdrawal (GA1).</summary>
/// <param name="Kind">None, until X days before, or with a fee.</param>
/// <param name="DaysBefore">For <see cref="RefundPolicyKind.UntilDaysBefore"/>: until this many days before the service date.</param>
/// <param name="FeeBasisPoints">For <see cref="RefundPolicyKind.WithFee"/>: the cancellation fee in basis points of the price.</param>
public sealed record RefundPolicy(RefundPolicyKind Kind, int? DaysBefore = null, int? FeeBasisPoints = null)
{
    public static RefundPolicy None { get; } = new(RefundPolicyKind.None);
}

/// <summary>An item's refund terms as a <see cref="IRefundTermsSource"/> answers them.</summary>
/// <param name="Policy">The seller's policy for the sale or product (clamped to the channel's bounds, D20).</param>
/// <param name="WithdrawalRight">
/// Whether the line carries the 14-day withdrawal right; null lets Checkout decide (physical goods: yes; a dated leisure
/// service: no, CRD Art. 16(l)).
/// </param>
/// <param name="WithdrawalExemption">When there is no right: the CRD Art. 16 point that exempts it (<c>16(c)</c> made to specification, <c>16(d)</c> perishable, <c>16(e)</c> sealed for hygiene, …), shown to the buyer.</param>
public sealed record RefundTerms(RefundPolicy? Policy = null, bool? WithdrawalRight = null, string? WithdrawalExemption = null);

/// <summary>
/// The seller refund policy and withdrawal terms of an item (D20, GA1). Shop and Ticketing.Events register one for what
/// they sell (scoped, <c>TryAddEnumerable</c>), a host for anything else (<c>[SparkDiscoverable]</c>). Asked when the order
/// is placed: the terms are stored with the order, so a later policy change never touches a sold order.
/// </summary>
[SparkDiscoverable]
public interface IRefundTermsSource
{
    bool Handles(string itemId);

    Task<RefundTerms?> ResolveAsync(string itemId, string channel, CancellationToken cancellationToken);
}

/// <summary>
/// The delivery part of an order (GA1, CRD Art. 13(2)): which order extras are delivery, and how much of them the least
/// expensive standard delivery would have cost. Shop/Shipping register one; without one, extras with code
/// <c>delivery</c> (or starting with <c>delivery:</c>) are delivery, and all of it counts as standard.
/// </summary>
[SparkDiscoverable]
public interface IStandardDeliveryCost
{
    /// <summary>The standard delivery cost of <paramref name="extra"/>, or null when this source does not know it.</summary>
    Task<Money?> GetStandardAsync(Models.CheckoutOrder order, Models.CheckoutOrderExtra extra, CancellationToken cancellationToken);
}

// ---- the order-event payloads (Invoicing M10, Tickets M15, Shop/Warehouse) ----------------------------------------

/// <summary>
/// A refund issued (<see cref="OrderEventKind.RefundIssued"/>): Invoicing's credit-note input. Every amount is gross (what
/// the buyer paid back). Per line it carries what S21 strategy H needs: this refund, the gross credited before, and the
/// line as sold (gross, net, VAT, rate). Invoicing computes the VAT split and keeps its own credited-VAT state; one credit
/// note per <see cref="RefundId"/> (redelivery is possible, recipients are idempotent).
/// </summary>
/// <param name="Deducted">What was kept from the share (a cancellation fee, diminished value): settled but not paid back.</param>
public sealed record OrderRefundIssued(
    string RefundId,
    string OrderId,
    string Currency,
    RefundKind Kind,
    ReturnReasonCode? Reason,
    Money Amount,
    Money Deducted,
    IReadOnlyList<RefundedLine> Lines,
    IReadOnlyList<RefundedExtra> Extras,
    DateTimeOffset IssuedAt,
    bool SettledManually);

/// <summary>One order line in a refund.</summary>
/// <param name="Quantity">Units returned by this refund (0 for an amount-only refund such as a price reduction).</param>
/// <param name="Gross">Paid back for the line by this refund (price part plus fees).</param>
/// <param name="PriceGross">The price part of <paramref name="Gross"/> (order discounts allocated in).</param>
/// <param name="Fees">The fee parts of <paramref name="Gross"/>, by fee code.</param>
/// <param name="Deducted">Kept from this line's share (cancellation fee, diminished value).</param>
/// <param name="CumulativeGrossBefore">The line's gross settled (paid back or kept) before this refund: S21's cumulative-share input.</param>
/// <param name="LineGross">The line's total as sold.</param>
public sealed record RefundedLine(
    string LineId,
    string ItemId,
    string? SellerOrganizationId,
    int Quantity,
    Money Gross,
    Money PriceGross,
    IReadOnlyList<RefundedFee> Fees,
    Money Deducted,
    Money CumulativeGrossBefore,
    Money LineGross,
    Money LineNet,
    Money LineVat,
    int? VatRateBasisPoints,
    string? VatCategory);

public sealed record RefundedFee(string Code, Money Gross);

/// <summary>An order extra (delivery, donation, insurance) in a refund.</summary>
public sealed record RefundedExtra(string Code, string Kind, Money Gross, Money CumulativeGrossBefore, Money ExtraGross, string? VatCategory);

/// <summary>Units whose tickets Tickets revokes (<see cref="OrderEventKind.TicketsRevoked"/>), exactly once per <see cref="RevocationId"/>.</summary>
public sealed record OrderTicketRevocation(string RevocationId, IReadOnlyList<RevokedLine> Lines);

/// <param name="Units">The seats revoked; empty for unnumbered units (revoke <paramref name="Quantity"/> of the line's tickets).</param>
public sealed record RevokedLine(string LineId, string ItemId, int Quantity, IReadOnlyList<string> Units);

/// <summary>Goods expected back (<see cref="OrderEventKind.ReturnExpected"/>): Shop mails a return label, the warehouse adds an expected return (warehouse §4.3).</summary>
public sealed record OrderReturnExpected(string ReturnId, ReturnReasonCode Reason, ReturnDisposition DefaultDisposition, bool SellerPaysReturnShipping, IReadOnlyList<ExpectedReturnLine> Lines);

public sealed record ExpectedReturnLine(string LineId, string ItemId, int Quantity);
