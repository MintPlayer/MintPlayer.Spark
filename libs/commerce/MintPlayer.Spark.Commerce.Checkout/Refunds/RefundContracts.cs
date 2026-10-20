using MintPlayer.Spark.Abstractions;

// The refund flows' TypeScript contracts, in the namespace of @mintplayer/ng-spark/commerce/checkout (X20 item 2).
// Ids and numbers travel in bodies and query strings, never in paths. Enumerations travel as their names.
namespace MintPlayer.Spark.Commerce.Checkout.Contracts;

/// <summary>Everything after the sale the order page shows (<c>GET /spark/commerce/checkout/refunds/order</c>).</summary>
/// <param name="Withdrawal">GA1: whether the withdrawal function is shown, and until when.</param>
/// <param name="RescheduleOffers">D20: open keep-or-refund windows that cover the order.</param>
[SparkContract]
public sealed record SparkOrderAftercare(
    string Number,
    string State,
    string Currency,
    SparkWithdrawalInfo Withdrawal,
    IReadOnlyList<SparkAftercareLine> Lines,
    IReadOnlyList<SparkAftercareReturn> Returns,
    IReadOnlyList<SparkAftercareClaim> Claims,
    IReadOnlyList<SparkAftercareRefund> Refunds,
    IReadOnlyList<SparkRescheduleOffer> RescheduleOffers);

/// <summary>GA1, CRD Art. 11a: the withdrawal function.</summary>
/// <param name="Available">Whether the button is shown: some line still carries a live right.</param>
/// <param name="Deadline">When the period ends (exclusive); null while goods are undelivered (the right is live, Art. 9(3)).</param>
/// <param name="ButtonKey">The button's translation key ("Withdraw from contract here").</param>
/// <param name="SellerPaysReturnShipping">Shown in the withdrawal information (CRD Art. 6(1)(i)).</param>
[SparkContract]
public sealed record SparkWithdrawalInfo(bool Available, DateTimeOffset? Deadline, string ButtonKey, string ConfirmKey, bool SellerPaysReturnShipping);

/// <summary>One order line after the sale.</summary>
/// <param name="Free">Units still free for a withdrawal, cancellation or claim.</param>
/// <param name="WithdrawalExemption">Why there is no withdrawal right (<c>16(l)</c>, <c>service</c>, a source's Art. 16 point).</param>
/// <param name="CancellationRefusal">D20: why the seller's policy refuses a cancellation now; null when it allows one.</param>
/// <param name="CancellationFeeBasisPoints">D20 <c>WithFee</c>: the fee kept.</param>
/// <param name="GuaranteeUntil">GA8: the end of the legal guarantee (null while undelivered).</param>
[SparkContract]
public sealed record SparkAftercareLine(
    string LineId,
    TranslatedString? Title,
    int Quantity,
    int Returned,
    int Free,
    bool WithdrawalRight,
    bool WithdrawalLive,
    string? WithdrawalExemption,
    string? CancellationRefusal,
    int? CancellationFeeBasisPoints,
    bool GuaranteeLive,
    DateTimeOffset? GuaranteeUntil);

/// <summary>A return (withdrawal, guarantee termination, seller return).</summary>
/// <param name="ReceivedAt">When the withdrawal was received: the receipt states it (CRD Art. 11a(4)).</param>
[SparkContract]
public sealed record SparkAftercareReturn(string ReturnId, string Reason, string Status, DateTimeOffset ReceivedAt, IReadOnlyList<SparkReturnLineView> Lines, DateTimeOffset? RefundDueBy);

/// <summary>A line of a return or claim, with whether it is within the right (and why not).</summary>
[SparkContract]
public sealed record SparkReturnLineView(string LineId, int Quantity, bool Eligible, string? IneligibleReason);

/// <summary>A legal-guarantee claim (GA8).</summary>
[SparkContract]
public sealed record SparkAftercareClaim(string ClaimId, string Status, string? Remedy, DateTimeOffset OpenedAt, IReadOnlyList<SparkReturnLineView> Lines);

/// <summary>A refund.</summary>
[SparkContract]
public sealed record SparkAftercareRefund(string RefundId, string Kind, string Status, Money Amount, DateTimeOffset CreatedAt, DateTimeOffset? IssuedAt);

/// <summary>D20: a reschedule window: until <paramref name="ClosesAt"/> the buyer chooses to keep or be refunded.</summary>
[SparkContract]
public sealed record SparkRescheduleOffer(string WindowId, DateTimeOffset ClosesAt, DateTimeOffset? NewServiceDate, string? Choice);

/// <summary>Units of a line to give back.</summary>
[SparkContract]
public sealed record SparkRefundLine(string LineId, int Quantity, IReadOnlyList<string>? Units = null);

/// <summary>
/// <c>POST /spark/commerce/checkout/refunds/withdraw</c> (GA1, CRD Art. 11a): <paramref name="Confirm"/> false answers the
/// preview (what is within the right, what is not), true records the withdrawal and mails the receipt. No reason is
/// required and no one approves it.
/// </summary>
/// <param name="Access">A guest's signed order link token.</param>
/// <param name="Lines">The lines withdrawn; null withdraws every line still free.</param>
/// <param name="Name">CRD Art. 11a(2)(a): the consumer's name (the order's when empty).</param>
/// <param name="Email">CRD Art. 11a(2)(c): where the acknowledgement goes (the order's when empty).</param>
[SparkContract]
public sealed record SparkWithdrawalRequest(
    string Number,
    string? Access = null,
    IReadOnlyList<SparkRefundLine>? Lines = null,
    string? Note = null,
    string? Name = null,
    string? Email = null,
    bool Confirm = false);

/// <summary>What a withdrawal (preview or recorded) answers.</summary>
/// <param name="Refund">What is refunded now (null while withheld until the goods are back, CRD Art. 13(3)).</param>
/// <param name="Withheld">Part of the refund waits for the goods or proof of dispatch.</param>
[SparkContract]
public sealed record SparkWithdrawalResult(
    bool Confirmed,
    string? ReturnId,
    DateTimeOffset? ReceivedAt,
    IReadOnlyList<SparkReturnLineView> Lines,
    Money? Refund,
    bool Withheld,
    DateTimeOffset? RefundDueBy);

/// <summary><c>POST /spark/commerce/checkout/refunds/cancel</c>: a cancellation within the seller's policy (D20); preview unless <paramref name="Confirm"/>.</summary>
[SparkContract]
public sealed record SparkCancellationRequest(string Number, string? Access = null, IReadOnlyList<SparkRefundLine>? Lines = null, bool Confirm = false);

/// <summary>A refund preview or result: paid back, kept (fee), and the lines.</summary>
[SparkContract]
public sealed record SparkRefundResult(bool Confirmed, Money Amount, Money Kept, IReadOnlyList<SparkRefundLine> Lines, IReadOnlyList<string> RefundIds);

/// <summary><c>POST /spark/commerce/checkout/refunds/claim</c>: a legal-guarantee claim (GA8, Directive (EU) 2019/771).</summary>
/// <param name="PreferredRemedy"><c>Repair</c> or <c>Replacement</c> (Art. 13(2): the consumer chooses between them); the seller decides price reduction or termination under Art. 13(4).</param>
[SparkContract]
public sealed record SparkGuaranteeClaimRequest(string Number, IReadOnlyList<SparkRefundLine> Lines, string Description, string? Access = null, string? PreferredRemedy = null);

/// <summary><c>POST /spark/commerce/checkout/refunds/reschedule</c>: keep (<c>keep</c>) or be refunded (<c>refund</c>) after a reschedule (D20).</summary>
[SparkContract]
public sealed record SparkRescheduleChoiceRequest(string Number, string WindowId, string Choice, string? Access = null);

// ---- staff ------------------------------------------------------------------------------------------------------------

/// <summary><c>POST …/refunds/staff/withdrawal</c>: a withdrawal received by mail (the model form, CRD Annex I(B)) or phone, with its receipt time.</summary>
[SparkContract]
public sealed record SparkStaffWithdrawalRequest(string OrderId, DateTimeOffset ReceivedAt, string Source, IReadOnlyList<SparkRefundLine>? Lines = null, string? Note = null, string? Name = null, string? Email = null);

/// <summary><c>POST …/refunds/staff/goods-back</c>: goods received (<c>Received</c>) or dispatch proven (<c>ProofOfDispatch</c>).</summary>
[SparkContract]
public sealed record SparkGoodsBackRequest(string OrderId, string ReturnId, string Kind, DateTimeOffset? At = null, string? Disposition = null, IReadOnlyList<SparkDeduction>? Deductions = null);

/// <summary>Diminished value (CRD Art. 14(2)) kept from a line, with the reason.</summary>
[SparkContract]
public sealed record SparkDeduction(string LineId, Money Amount, string Reason);

/// <summary><c>POST …/refunds/staff/refund</c>: a return or goodwill refund by reason (<c>WrongItem</c>, <c>DamagedInTransit</c>, <c>SellerAllowed</c>, <c>Other</c>).</summary>
/// <param name="Amount">An amount-only refund over <paramref name="Lines"/>' price (or every line); otherwise units.</param>
[SparkContract]
public sealed record SparkStaffRefundRequest(string OrderId, string Reason, IReadOnlyList<SparkRefundLine>? Lines = null, Money? Amount = null, string? Note = null);

/// <summary><c>POST …/refunds/staff/claim</c>: the remedy of a guarantee claim; <c>PriceReduction</c> and <c>Refund</c> need an Art. 13(4) ground.</summary>
[SparkContract]
public sealed record SparkClaimResolutionRequest(string OrderId, string ClaimId, string Remedy, string? Ground = null, Money? Amount = null, string? Note = null);

/// <summary><c>POST …/refunds/staff/retry</c> and <c>…/settle</c>: retry a failed refund (D20) or settle it by hand (finance, audited).</summary>
[SparkContract]
public sealed record SparkRefundActionRequest(string OrderId, string RefundId, string? Note = null);

/// <summary><c>POST …/refunds/staff/mass</c>: the event was cancelled: refund every remaining unit of the items (D20).</summary>
/// <param name="RefundFees">Whether fees are refunded; null takes the channel's setting.</param>
[SparkContract]
public sealed record SparkMassRefundRequest(IReadOnlyList<string> ItemIds, string? OrganizationId = null, string? Reason = null, bool? RefundFees = null);

/// <summary><c>POST …/refunds/staff/mass/control</c>: <c>pause</c>, <c>resume</c>, <c>cancel</c> or <c>retryFailed</c>.</summary>
[SparkContract]
public sealed record SparkMassRefundControlRequest(string MassRefundId, string Action);

/// <summary>A mass refund's progress (exact counters, S15).</summary>
[SparkContract]
public sealed record SparkMassRefundView(
    string Id,
    string Mode,
    string Status,
    int Total,
    int Processed,
    int Skipped,
    int Refunds,
    int Pending,
    int Succeeded,
    int Failed,
    int InsufficientBalance,
    int ManualRequired,
    int SettledManually,
    int Notified,
    Money? AmountRequested,
    string? LastError);

/// <summary><c>POST …/refunds/staff/reschedule</c>: open a keep-or-refund window for the items and mail every buyer (D20).</summary>
[SparkContract]
public sealed record SparkRescheduleWindowRequest(IReadOnlyList<string> ItemIds, DateTimeOffset ClosesAt, string? OrganizationId = null, DateTimeOffset? NewServiceDate = null, string? Note = null);

/// <summary>The window and the notice run that mails it.</summary>
[SparkContract]
public sealed record SparkRescheduleWindowResult(string WindowId, string MassRefundId);
