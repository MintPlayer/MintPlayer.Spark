using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Commerce.Checkout.Refunds;

/// <summary>
/// Refunds and after-sale for other packages and hosts (PRD §4.7): Shipping/Fulfilment report delivery (the withdrawal
/// and guarantee periods start there), Shop and the warehouse report goods back (the withheld withdrawal refund is then
/// paid, CRD Art. 13(3)) and their disposition (restock, I4). Registered scoped by <c>AddCommerceCheckout()</c>. The
/// buyer's and staff's flows are the endpoints under <c>/spark/commerce/checkout/refunds</c>.
/// </summary>
public interface ISparkCheckoutRefunds
{
    /// <summary>The buyer received the goods of a line: the withdrawal (CRD Art. 9(2)(b)) and guarantee (Directive 2019/771 Art. 10(1)) periods count from here.</summary>
    Task MarkDeliveredAsync(string orderId, string lineId, DateTimeOffset deliveredAt, CancellationToken cancellationToken = default);

    /// <summary>
    /// Goods of a return came back (<see cref="GoodsBackKind.Received"/>) or the buyer proved dispatch: a refund withheld
    /// under CRD Art. 13(3) is paid now (whichever comes first). On receipt, a <see cref="ReturnDisposition.Restock"/>
    /// disposition puts the units back on sale exactly once (I4); diminished value (CRD Art. 14(2)) may be deducted, with
    /// a reason, before the refund is paid.
    /// </summary>
    Task RegisterGoodsBackAsync(GoodsBackRegistration registration, CancellationToken cancellationToken = default);
}

/// <summary>
/// Refunds goods the seller cannot deliver (lead decision 2026-10-20, commerce PRD §13): a warehouse cancelled units on its
/// own (a cancelled remainder, warehouse §4.3). Registered scoped by <c>AddCommerceCheckout()</c>; Commerce.Fulfilment
/// calls it. CRD Art. 18(2)–(3): where the trader fails to deliver and the contract is terminated, the trader reimburses "all
/// sums paid under the contract" without undue delay, delivery costs included (checked against EUR-Lex
/// 02011L0083-20260927, 2026-10-20).
/// </summary>
public interface ISparkCheckoutUndelivered
{
    /// <summary>
    /// Refunds the units' charged amounts (with their order-discount share and their fees) and, when no goods of the order
    /// remain, the whole delivery cost; the buyer gets the refund mail once it is paid. No quota goes back on sale here: a
    /// stock feed's line is restocked by the feed (warehouse §4.4 rule 1). Idempotent per <see cref="UndeliveredRefund.Reference"/>;
    /// answers the refund id, or null when nothing was left to refund.
    /// </summary>
    Task<string?> RefundUndeliveredAsync(UndeliveredRefund request, CancellationToken cancellationToken = default);
}

/// <summary>Goods that will not be delivered.</summary>
/// <param name="Reference">Stable per fact (e.g. the warehouse's cumulative cancelled count of a line): a repeat refunds nothing.</param>
public sealed record UndeliveredRefund(string OrderId, string Reference, IReadOnlyList<UndeliveredLine> Lines);

public sealed record UndeliveredLine(string LineId, int Quantity);

public enum GoodsBackKind
{
    Received,
    ProofOfDispatch,
    /// <summary>
    /// Neither yet: the seller pays the withheld refund now, without waiting for the goods (a host's "refund on request",
    /// warehouse PRD WD12, commerce §4.9). No date is recorded; the receipt is still registered when the goods arrive.
    /// </summary>
    Released,
}

/// <summary>Goods of a return came back, or dispatch was proven.</summary>
/// <param name="Disposition">On receipt: what happens to the goods; null takes the reason's default.</param>
/// <param name="Deductions">On receipt, before the refund is paid: diminished value per line, with a reason (audited).</param>
/// <param name="ActorId">Who registered it (null for a system call, e.g. the warehouse's receipt event).</param>
public sealed record GoodsBackRegistration(
    string OrderId,
    string ReturnId,
    GoodsBackKind Kind,
    DateTimeOffset At,
    ReturnDisposition? Disposition = null,
    IReadOnlyList<DiminishedValue>? Deductions = null,
    string? ActorId = null);

/// <summary>A diminished-value deduction (CRD Art. 14(2)): handling beyond what was needed to establish the goods' nature.</summary>
public sealed record DiminishedValue(string LineId, Money Amount, string Reason);
