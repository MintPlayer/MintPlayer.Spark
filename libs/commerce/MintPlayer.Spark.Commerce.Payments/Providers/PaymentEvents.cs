using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Commerce.Payments;

/// <summary>
/// Something a provider reported, parsed from a webhook (<see cref="IPaymentProvider.ParseWebhookAsync"/>). Events may
/// arrive late, twice or out of order (research §4.3): <see cref="ISparkPayments"/> records them idempotently by
/// <see cref="EventKey"/> and never moves a payment back from a final state.
/// </summary>
/// <param name="Provider">The provider.</param>
/// <param name="EventKey">What makes the event unique for deduplication (Stripe <c>evt_…</c>; for Mollie the resource id
/// plus its fetched state, since Mollie sends one id for every change).</param>
/// <param name="OccurredAt">When the provider says it happened.</param>
public abstract record PaymentProviderEvent(string Provider, string EventKey, DateTimeOffset OccurredAt);

/// <summary>A payment's status (or amounts) changed.</summary>
public sealed record PaymentChangedEvent(string Provider, string EventKey, DateTimeOffset OccurredAt, ProviderPaymentSnapshot Payment)
    : PaymentProviderEvent(Provider, EventKey, OccurredAt);

/// <summary>A refund's status changed (a refund that failed for lack of balance included, D20).</summary>
public sealed record RefundChangedEvent(string Provider, string EventKey, DateTimeOffset OccurredAt, ProviderRefundSnapshot Refund)
    : PaymentProviderEvent(Provider, EventKey, OccurredAt);

/// <summary>A dispute or chargeback was opened, changed or closed (GB1).</summary>
public sealed record DisputeChangedEvent(string Provider, string EventKey, DateTimeOffset OccurredAt, ProviderDisputeSnapshot Dispute)
    : PaymentProviderEvent(Provider, EventKey, OccurredAt);

/// <summary>A connected seller's account changed: verification progressed, charges or payouts were enabled or disabled (D21).</summary>
public sealed record ConnectedAccountChangedEvent(string Provider, string EventKey, DateTimeOffset OccurredAt, ConnectedAccountSnapshot Account)
    : PaymentProviderEvent(Provider, EventKey, OccurredAt);

/// <summary>The state of a dispute (GB1), unified over Stripe's dispute statuses and Mollie's chargebacks.</summary>
public enum DisputeStatus
{
    /// <summary>An inquiry or early warning; no money has moved yet (Stripe <c>warning_needs_response</c>, <c>warning_under_review</c>).</summary>
    Warning,

    /// <summary>Opened; evidence can be submitted until <see cref="ProviderDisputeSnapshot.EvidenceDueBy"/>. A Mollie
    /// chargeback starts here: its funds are already withdrawn and it can only be contested in Mollie's dashboard.</summary>
    NeedsResponse,

    /// <summary>Evidence was submitted; the issuer decides (Stripe <c>under_review</c>).</summary>
    UnderReview,

    /// <summary>Decided for the merchant (Stripe <c>won</c>, Mollie chargeback reversed). Tickets are unfrozen.</summary>
    Won,

    /// <summary>Decided for the buyer (Stripe <c>lost</c>; a Mollie chargeback the seller accepted or that passed its
    /// finality window). I4 releases what is releasable, keyed by the dispute id.</summary>
    Lost,

    /// <summary>An inquiry closed without a dispute (Stripe <c>warning_closed</c>).</summary>
    WarningClosed,
}

/// <summary>A dispute as the provider reported it.</summary>
public sealed record ProviderDisputeSnapshot
{
    public required string Provider { get; init; }

    /// <summary>Stripe <c>dp_…</c>, Mollie <c>chb_…</c>.</summary>
    public required string ProviderDisputeId { get; init; }

    /// <summary>The disputed payment, as stored by us (Mollie <c>tr_…</c>; Stripe the PaymentIntent <c>pi_…</c> or charge <c>ch_…</c>).</summary>
    public required string ProviderPaymentId { get; init; }

    public required DisputeStatus Status { get; init; }

    /// <summary>The disputed amount.</summary>
    public required Money Amount { get; init; }

    /// <summary>The provider's reason code (<c>fraudulent</c>, <c>product_not_received</c>, a card-scheme code for Mollie).</summary>
    public string? Reason { get; init; }

    /// <summary>The provider's reason text, when it gives one.</summary>
    public string? ReasonText { get; init; }

    /// <summary>The deadline for evidence; null when the provider gives none (Mollie).</summary>
    public DateTimeOffset? EvidenceDueBy { get; init; }

    /// <summary>Whether the disputed funds have been withdrawn from the merchant's balance (the Connect debit, GB1).</summary>
    public bool FundsWithdrawn { get; init; }

    /// <summary>Whether withdrawn funds were given back (dispute won, chargeback reversed).</summary>
    public bool FundsReinstated { get; init; }

    /// <summary>The provider's dispute fee charged to the merchant, when reported.</summary>
    public Money? Fee { get; init; }

    public DateTimeOffset? CreatedAt { get; init; }
}

/// <summary>A connected seller's account as the provider reported it (D21).</summary>
public sealed record ConnectedAccountSnapshot
{
    public required string Provider { get; init; }

    /// <summary>Stripe <c>acct_…</c>, Mollie <c>org_…</c>.</summary>
    public required string AccountId { get; init; }

    /// <summary>The verification state Commerce's KYC gate reads.</summary>
    public required KycStatus Kyc { get; init; }

    /// <summary>The account can take payments.</summary>
    public bool ChargesEnabled { get; init; }

    /// <summary>The provider pays out to the account.</summary>
    public bool PayoutsEnabled { get; init; }

    /// <summary>What the provider still needs from the seller (Stripe <c>requirements.currently_due</c>; Mollie <c>needs-data</c>).</summary>
    public IReadOnlyList<string> Requirements { get; init; } = [];

    /// <summary>The provider's reason for disabling the account, when it says (Stripe <c>requirements.disabled_reason</c>).</summary>
    public string? DisabledReason { get; init; }

    /// <summary>The account's display name at the provider.</summary>
    public string? Name { get; init; }
}
