using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Commerce.Payments;

/// <summary>
/// Imports a provider's settlement or payout report (GB4): Stripe payouts with their balance transactions, Mollie
/// settlements with their payments, refunds, chargebacks and costs. One per adapter. The payments package matches each
/// line to an order payment, a refund, a dispute or a fee; what does not match lands in the reconciliation queue for
/// the <c>finance</c> role.
/// </summary>
public interface ISettlementSource
{
    /// <summary>The provider.</summary>
    string Provider { get; }

    /// <summary>
    /// The settlements (payouts) of <paramref name="merchant"/>'s account created after <paramref name="since"/>, oldest
    /// first, each with all its lines. A settlement still open at the provider is skipped until it is final, so a line is
    /// imported once.
    /// </summary>
    IAsyncEnumerable<ProviderSettlement> GetSettlementsAsync(MerchantContext merchant, DateTimeOffset since, CancellationToken cancellationToken);
}

/// <summary>One settlement or payout.</summary>
/// <param name="SettlementId">Stripe <c>po_…</c>, Mollie <c>stl_…</c>.</param>
/// <param name="Reference">The bank reference the payout carries, when the provider gives one.</param>
/// <param name="CreatedAt">When the provider created it (the import cursor).</param>
/// <param name="SettledAt">When it was paid out, if it was.</param>
/// <param name="Amount">What reached (or will reach) the bank account; negative when the merchant owes the provider.</param>
/// <param name="Lines">Every line it is made of.</param>
public sealed record ProviderSettlement(
    string SettlementId,
    string? Reference,
    DateTimeOffset CreatedAt,
    DateTimeOffset? SettledAt,
    Money Amount,
    IReadOnlyList<ProviderSettlementLine> Lines);

/// <summary>What a settlement line is.</summary>
public enum SettlementLineKind
{
    /// <summary>A buyer's payment.</summary>
    Payment,

    /// <summary>A refund to a buyer.</summary>
    Refund,

    /// <summary>A dispute or chargeback debit.</summary>
    Chargeback,

    /// <summary>A dispute won or a chargeback reversed.</summary>
    ChargebackReversal,

    /// <summary>The provider's own fee (transaction costs, dispute fees, monthly costs).</summary>
    ProviderFee,

    /// <summary>The platform's application fee (D5).</summary>
    ApplicationFee,

    /// <summary>An application fee given back with a refund.</summary>
    ApplicationFeeRefund,

    /// <summary>A transfer between accounts (D40 split).</summary>
    Transfer,

    /// <summary>The payout itself (the settlement's own line).</summary>
    Payout,

    /// <summary>Anything else (adjustments, reserves, top-ups).</summary>
    Other,
}

/// <summary>One line of a settlement.</summary>
/// <param name="LineId">Unique within the provider (Stripe <c>txn_…</c>; Mollie the resource id plus its kind).</param>
/// <param name="Kind">What it is.</param>
/// <param name="SourceId">The resource it is about: a payment (<c>tr_…</c>, <c>ch_…</c>, <c>pi_…</c>), refund (<c>re_…</c>),
/// dispute (<c>dp_…</c>, <c>chb_…</c>) or fee (<c>fee_…</c>); null for costs without a source.</param>
/// <param name="Gross">The line's amount before the provider's fee (signed: negative for money leaving the merchant).</param>
/// <param name="Fee">The provider's fee on this line (positive = charged), when reported per line.</param>
/// <param name="Net">What the line contributes to the payout.</param>
/// <param name="OccurredAt">When it happened.</param>
/// <param name="Description">The provider's text.</param>
public sealed record ProviderSettlementLine(
    string LineId,
    SettlementLineKind Kind,
    string? SourceId,
    Money Gross,
    Money? Fee,
    Money Net,
    DateTimeOffset OccurredAt,
    string? Description);
