using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Commerce.Checkout.Refunds;

/// <summary>
/// Everything after the sale for one order (PRD §4.7): the refund terms fixed at purchase, what each line has given back,
/// the refunds, the returns (withdrawals included, GA1) and the guarantee claims (GA8). One document per order, id
/// <c>CheckoutRefundLedgers/{orderId}</c>, always saved <b>in the same session as the order</b> (optimistic concurrency),
/// so the order's line statuses, the ledger and a mass refund's counters change together or not at all (I4, S15).
/// </summary>
public sealed class CheckoutRefundLedger
{
    public string? Id { get; set; }

    public string OrderId { get; set; } = "";

    public string OrderNumber { get; set; } = "";

    /// <summary>The order's sellers (row security, like the order).</summary>
    public List<string> Sellers { get; set; } = [];

    public string Currency { get; set; } = "EUR";

    /// <summary>When the terms were captured (at placement, or lazily for an older order).</summary>
    public DateTimeOffset TermsCapturedAt { get; set; }

    public List<RefundLedgerLine> Lines { get; set; } = [];

    /// <summary>Every refundable component of the order: line prices, line fees, extras (<see cref="RefundComponent"/>).</summary>
    public List<RefundComponent> Components { get; set; } = [];

    public List<CheckoutRefund> Refunds { get; set; } = [];

    public List<CheckoutReturn> Returns { get; set; } = [];

    public List<CheckoutGuaranteeClaim> Claims { get; set; } = [];

    /// <summary>Reschedule windows the buyer answered (D20), by window id: <c>keep</c> or <c>refund</c>.</summary>
    public Dictionary<string, string> RescheduleChoices { get; set; } = new(StringComparer.Ordinal);

    public DateTimeOffset UpdatedAt { get; set; }

    public static string IdFor(string orderId) => "CheckoutRefundLedgers/" + orderId;
}

/// <summary>An order line's after-sale state.</summary>
public sealed class RefundLedgerLine
{
    public string LineId { get; set; } = "";

    public string ItemId { get; set; } = "";

    /// <summary>The seller policy fixed at purchase, clamped to the channel's bounds (D20).</summary>
    public RefundPolicy Policy { get; set; } = RefundPolicy.None;

    /// <summary>Whether the line carries the statutory withdrawal right (GA1).</summary>
    public bool WithdrawalRight { get; set; }

    /// <summary>Why it does not: <c>16(l)</c> for a dated leisure service, a source's exemption, <c>service</c>.</summary>
    public string? WithdrawalExemption { get; set; }

    /// <summary>When the buyer received the goods (Shipping/Fulfilment call <see cref="ISparkCheckoutRefunds.MarkDeliveredAsync"/>).</summary>
    public DateTimeOffset? DeliveredAt { get; set; }

    /// <summary>Units given back so far (refunded, cancelled, withdrawn, terminated).</summary>
    public int QuantityReturned { get; set; }

    /// <summary>Seats given back so far.</summary>
    public List<string> UnitsReturned { get; set; } = [];

    /// <summary>Units withdrawn or claimed whose refund still waits (goods not back yet): not available to another return.</summary>
    public int QuantityPending { get; set; }
}

/// <summary>
/// A refundable amount as sold: <c>line:{id}:price</c> (the line's price part, order discounts allocated in),
/// <c>line:{id}:fee:{code}</c>, <c>extra:{code}</c>. <see cref="Settled"/> grows by every refund and every deduction
/// (cancellation fee, diminished value); it never exceeds <see cref="Gross"/>. Cumulative shares (S21) are taken of it.
/// </summary>
public sealed class RefundComponent
{
    public string Key { get; set; } = "";

    public string? LineId { get; set; }

    /// <summary><c>price</c>, <c>fee</c> or <c>extra</c>.</summary>
    public string Part { get; set; } = "";

    /// <summary>The fee or extra code.</summary>
    public string? Code { get; set; }

    /// <summary>For an extra: its kind (<c>Donation</c>, <c>Insurance</c>, <c>Other</c>, …); <c>delivery</c> when it is delivery.</summary>
    public string? ExtraKind { get; set; }

    public Money Gross { get; set; } = Money.Zero("EUR");

    /// <summary>For delivery: the standard delivery cost (CRD Art. 13(2)).</summary>
    public Money? StandardGross { get; set; }

    public Money Settled { get; set; } = Money.Zero("EUR");

    public Money Refunded { get; set; } = Money.Zero("EUR");

    public Money Deducted { get; set; } = Money.Zero("EUR");

    public Money Remaining() => Gross - Settled;
}

/// <summary>One refund (D20): what it pays back, component by component, and where the provider stands.</summary>
public sealed class CheckoutRefund
{
    /// <summary>Unique in the order; also the provider reference suffix and the inventory return id (idempotency).</summary>
    public string RefundId { get; set; } = "";

    public RefundKind Kind { get; set; }

    public ReturnReasonCode? Reason { get; set; }

    public RefundStatus Status { get; set; }

    public Money Amount { get; set; } = Money.Zero("EUR");

    public Money Deducted { get; set; } = Money.Zero("EUR");

    public List<RefundPart> Parts { get; set; } = [];

    public List<RefundUnits> Lines { get; set; } = [];

    /// <summary>The provider reference (<c>{orderId}:refund:{refundId}</c>).</summary>
    public string? ProviderReference { get; set; }

    public string? PaymentId { get; set; }

    public int Attempt { get; set; }

    public string? FailureReason { get; set; }

    public string? FailureMessage { get; set; }

    public string? Note { get; set; }

    public string? CreatedBy { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>The refund is due by then (withdrawal: 14 days from the withdrawal, CRD Art. 13(1)).</summary>
    public DateTimeOffset? DueBy { get; set; }

    public DateTimeOffset? IssuedAt { get; set; }

    /// <summary>When <c>RefundIssued</c> reached every recipient; the sweep re-delivers until then.</summary>
    public DateTimeOffset? IssuedDeliveredAt { get; set; }

    public string? MassRefundId { get; set; }

    public string? ReturnId { get; set; }

    public string? ClaimId { get; set; }

    public string? SettledBy { get; set; }
}

/// <summary>One component's share of a refund.</summary>
/// <remarks><see cref="Gross"/> is paid back; <see cref="Deducted"/> is kept; both are settled. <see cref="SettledBefore"/> is the component's settled amount before (S21 input).</remarks>
public sealed class RefundPart
{
    public string Key { get; set; } = "";
    public Money Gross { get; set; } = Money.Zero("EUR");
    public Money Deducted { get; set; } = Money.Zero("EUR");
    public Money SettledBefore { get; set; } = Money.Zero("EUR");
}

/// <summary>Units of a line a refund gives back.</summary>
public sealed class RefundUnits
{
    public string LineId { get; set; } = "";
    public int Quantity { get; set; }
    public List<string> Units { get; set; } = [];
    /// <summary>Whether the quota units went back on sale at once (false for shipped goods: restock later, I4).</summary>
    public bool StockReturned { get; set; }
}

/// <summary>How a return (or withdrawal) was received.</summary>
public enum ReturnSource
{
    /// <summary>The withdrawal function or the order page (GA1).</summary>
    Online,
    /// <summary>A letter, e.g. the model withdrawal form (CRD Annex I(B)), registered by staff.</summary>
    Mail,
    Phone,
    /// <summary>Staff opened it (a wrong item, a damaged parcel).</summary>
    Staff,
}

public enum ReturnStatus
{
    /// <summary>Recorded; the refund (if any) is underway.</summary>
    Requested,
    /// <summary>The refund is withheld until the goods are back or dispatch is proven (CRD Art. 13(3)).</summary>
    AwaitingGoods,
    /// <summary>Refunded (or nothing to refund).</summary>
    Refunded,
    /// <summary>Recorded without a refund (every line outside the right, a guarantee repair).</summary>
    Closed,
}

/// <summary>A return of order lines: a withdrawal (GA1), a guarantee termination (GA8) or a seller return.</summary>
public sealed class CheckoutReturn
{
    public string ReturnId { get; set; } = "";

    public ReturnReasonCode Reason { get; set; }

    public ReturnStatus Status { get; set; }

    public ReturnSource Source { get; set; }

    /// <summary>When the withdrawal was received (the buyer's submission, or the letter's receipt time staff entered): the receipt states it.</summary>
    public DateTimeOffset ReceivedAt { get; set; }

    public DateTimeOffset RecordedAt { get; set; }

    public string? RecordedBy { get; set; }

    /// <summary>The buyer's optional reason (a withdrawal never needs one).</summary>
    public string? Note { get; set; }

    /// <summary>CRD Art. 11a(2): the consumer's name and the means of communication the acknowledgement goes to.</summary>
    public string? Name { get; set; }

    public string? Email { get; set; }

    public List<CheckoutReturnLine> Lines { get; set; } = [];

    public ReturnDisposition DefaultDisposition { get; set; }

    public bool SellerPaysReturnShipping { get; set; }

    public DateTimeOffset? GoodsReceivedAt { get; set; }

    public DateTimeOffset? ProofOfDispatchAt { get; set; }

    public ReturnDisposition? Disposition { get; set; }

    /// <summary>Diminished value deducted after receipt (CRD Art. 14(2)), with reason; audited.</summary>
    public List<CheckoutDeduction> Deductions { get; set; } = [];

    /// <summary>The refund is due by then (CRD Art. 13(1): 14 days from the withdrawal).</summary>
    public DateTimeOffset? RefundDueBy { get; set; }

    public List<string> RefundIds { get; set; } = [];

    public DateTimeOffset? ReceiptSentAt { get; set; }

    public string? ClaimId { get; set; }

    /// <summary>Whether the quota units of the shipped lines were restocked (I4).</summary>
    public bool Restocked { get; set; }
}

public sealed class CheckoutReturnLine
{
    public string LineId { get; set; } = "";

    public int Quantity { get; set; }

    public List<string> Units { get; set; } = [];

    /// <summary>Whether the request is within the right (withdrawal period, guarantee period, not exempt).</summary>
    public bool Eligible { get; set; }

    /// <summary>Why not, shown to the buyer before confirming and to staff: <c>periodEnded</c>, <c>exempt:16(l)</c>, …</summary>
    public string? IneligibleReason { get; set; }

    /// <summary>The goods were shipped: the refund may wait for them (CRD Art. 13(3)) and the quota waits for restock (I4).</summary>
    public bool Shipped { get; set; }
}

public sealed class CheckoutDeduction
{
    public string LineId { get; set; } = "";
    public Money Amount { get; set; } = Money.Zero("EUR");
    public string Reason { get; set; } = "";
    public string? By { get; set; }
    public DateTimeOffset At { get; set; }
}

/// <summary>The remedies of Directive (EU) 2019/771 Art. 13.</summary>
public enum GuaranteeRemedy
{
    Repair,
    Replacement,
    PriceReduction,
    /// <summary>Termination of the contract (Art. 16): the price is refunded once the goods are back or dispatch is proven.</summary>
    Refund,
}

/// <summary>
/// The grounds of Art. 13(4) that open a price reduction or termination (the hierarchy: repair or replacement first).
/// </summary>
public enum GuaranteeGround
{
    /// <summary>13(4)(a): the seller did not repair or replace, or refused (Art. 13(3)).</summary>
    NotRepairedOrReplaced,
    /// <summary>13(4)(b): the lack of conformity appears despite the attempt.</summary>
    PersistsAfterAttempt,
    /// <summary>13(4)(c): serious enough for an immediate reduction or termination.</summary>
    Serious,
    /// <summary>13(4)(d): the seller declared, or it is clear, that it will not bring the goods into conformity.</summary>
    SellerWillNot,
}

public enum GuaranteeClaimStatus
{
    Open,
    /// <summary>Termination decided: the refund waits for the goods (Art. 16(3)).</summary>
    AwaitingGoods,
    Resolved,
}

/// <summary>A legal-guarantee claim (GA8). Not a withdrawal: no 14-day clock, no unconditional refund.</summary>
public sealed class CheckoutGuaranteeClaim
{
    public string ClaimId { get; set; } = "";
    public GuaranteeClaimStatus Status { get; set; }
    public List<CheckoutReturnLine> Lines { get; set; } = [];
    public string Description { get; set; } = "";
    public GuaranteeRemedy? PreferredRemedy { get; set; }
    public GuaranteeRemedy? Remedy { get; set; }
    public GuaranteeGround? Ground { get; set; }
    public ReturnSource Source { get; set; }
    public DateTimeOffset OpenedAt { get; set; }
    public string? OpenedBy { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
    public string? ResolvedBy { get; set; }
    public string? ResolutionNote { get; set; }
    public string? ReturnId { get; set; }
    public string? RefundId { get; set; }
}

// ---- mass refunds (D20, S15) ---------------------------------------------------------------------------------------

public enum MassRefundStatus
{
    /// <summary>The selection is being stored in chunks.</summary>
    Collecting,
    Running,
    Paused,
    Completed,
    Cancelled,
}

public enum MassRefundMode
{
    /// <summary>The event was cancelled: refund every remaining unit of the items.</summary>
    Refund,
    /// <summary>The event was rescheduled: mail every buyer the keep-or-refund offer of a window.</summary>
    RescheduleNotice,
}

/// <summary>
/// A mass refund (D20): batched, resumable, idempotent, with progress. The selection is fixed once in chunks
/// (<see cref="CheckoutMassRefundChunk"/>); a cursor walks it. Each order's refund, its ledger, the order and this
/// document's counters and cursor are saved in <b>one</b> session, so a crash leaves either everything or nothing of an
/// order, and the provider call repeated on resume is the same idempotent reference (S15).
/// </summary>
public sealed class CheckoutMassRefund
{
    public string? Id { get; set; }

    public MassRefundMode Mode { get; set; }

    public MassRefundStatus Status { get; set; }

    /// <summary>The seller whose orders it covers; null for the platform's own sale.</summary>
    public string? OrganizationId { get; set; }

    public List<string> ItemIds { get; set; } = [];

    public string? Reason { get; set; }

    /// <summary>Whether fees are refunded; null takes the channel's <c>RefundFees</c>.</summary>
    public bool? RefundFees { get; set; }

    /// <summary>For <see cref="MassRefundMode.RescheduleNotice"/>: the window.</summary>
    public string? WindowId { get; set; }

    public string? CreatedBy { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    public int ChunkCount { get; set; }

    /// <summary>Order ids per chunk (every chunk but the last is full).</summary>
    public int ChunkSize { get; set; }

    /// <summary>Whether the selection was stored (resume goes back to <see cref="MassRefundStatus.Running"/>, not collecting).</summary>
    public bool Collected { get; set; }

    public int Total { get; set; }

    public int NextChunk { get; set; }

    public int NextIndex { get; set; }

    public MassRefundCounters Counters { get; set; } = new();

    public DateTimeOffset? LastRunAt { get; set; }

    public string? LastError { get; set; }
}

/// <summary>Exact counters: each changes in the same transaction as the refund it counts.</summary>
public sealed class MassRefundCounters
{
    /// <summary>Orders the cursor passed.</summary>
    public int Processed { get; set; }
    /// <summary>Orders with nothing left to refund (already refunded, disputed, voided).</summary>
    public int Skipped { get; set; }
    /// <summary>Refunds created.</summary>
    public int Refunds { get; set; }
    public int Pending { get; set; }
    public int Succeeded { get; set; }
    public int Failed { get; set; }
    /// <summary>Of <see cref="Failed"/>: the seller's balance could not cover it (D20).</summary>
    public int InsufficientBalance { get; set; }
    public int ManualRequired { get; set; }
    public int SettledManually { get; set; }
    /// <summary>Reschedule notices mailed.</summary>
    public int Notified { get; set; }
    public Money? AmountRequested { get; set; }
}

/// <summary>One stored piece of a mass refund's selection, id <c>{massId}/chunks/{n}</c>.</summary>
public sealed class CheckoutMassRefundChunk
{
    public string? Id { get; set; }
    public List<string> OrderIds { get; set; } = [];

    public static string IdFor(string massId, int n) => massId + "/chunks/" + n;
}

/// <summary>A reschedule window (D20): until <see cref="ClosesAt"/>, buyers of the items choose to keep or be refunded.</summary>
public sealed class CheckoutRescheduleWindow
{
    public string? Id { get; set; }
    public string? OrganizationId { get; set; }
    public List<string> ItemIds { get; set; } = [];
    public DateTimeOffset OpensAt { get; set; }
    public DateTimeOffset ClosesAt { get; set; }
    public DateTimeOffset? NewServiceDate { get; set; }
    public string? Note { get; set; }
    public string? CreatedBy { get; set; }
    public string? NoticeMassRefundId { get; set; }
}
