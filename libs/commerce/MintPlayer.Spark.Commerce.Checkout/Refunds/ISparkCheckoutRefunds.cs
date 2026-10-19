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

public enum GoodsBackKind
{
    Received,
    ProofOfDispatch,
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
