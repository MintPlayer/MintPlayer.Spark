using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Commerce.Payments.Models;

/// <summary>
/// One payment created at a provider, as we recorded it (§4.5). Its id is natural,
/// <c>ProviderPayments/{provider}/{providerPaymentId}</c>, so a webhook that names a provider id loads it without a
/// query and a second record for the same payment cannot exist. It remembers the merchant (seller and account) the
/// payment was created for, which is how a Mollie webhook is fetched with the right credentials. Written only by
/// <see cref="ISparkPayments"/>; read by the seller's <c>finance</c> members and the platform's.
/// </summary>
public class ProviderPayment
{
    public string? Id { get; set; }

    public string Provider { get; set; } = string.Empty;

    public string ProviderPaymentId { get; set; } = string.Empty;

    /// <summary>Other provider ids of the same payment (Stripe <c>pi_…</c>, <c>ch_…</c>): settlement and dispute lines name these.</summary>
    public List<string> RelatedIds { get; set; } = [];

    /// <summary>Our reference: the order (Checkout's id).</summary>
    public string Reference { get; set; } = string.Empty;

    /// <summary>The seller; null for the platform's own sale.</summary>
    public string? SellerOrganizationId { get; set; }

    /// <summary>The connected account the payment was created on; null for the platform account.</summary>
    public string? AccountId { get; set; }

    public Money Amount { get; set; } = Money.Zero("EUR");

    public Money? AmountRefunded { get; set; }

    public ProviderPaymentStatus Status { get; set; } = ProviderPaymentStatus.Open;

    public string? Method { get; set; }

    public PaymentCaptureMode Capture { get; set; }

    public DateTime? ExpiresAtUtc { get; set; }

    public string? CheckoutUrl { get; set; }

    /// <summary>The platform's fee on this payment (D5).</summary>
    public Money? ApplicationFee { get; set; }

    /// <summary>The split across sellers (D40), when there is one.</summary>
    public List<PaymentSplitRecord> Splits { get; set; } = [];

    /// <summary>The payer fingerprint (D29 limit key). Never an attribute: not shown to anyone.</summary>
    public string? PayerFingerprint { get; set; }

    public bool? Prepaid { get; set; }

    /// <summary>The idempotency key the create was sent with.</summary>
    public string IdempotencyKey { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; }

    public DateTime? PaidAtUtc { get; set; }

    public DateTime UpdatedAtUtc { get; set; }

    /// <summary>The refunds made on it, by our reference.</summary>
    public List<ProviderPaymentRefund> Refunds { get; set; } = [];

    /// <summary>Whether the cancel the hold's death asked for was sent (D3).</summary>
    public bool CancelRequested { get; set; }

    /// <summary>Natural id of a provider payment.</summary>
    public static string IdFor(string provider, string providerPaymentId) => $"ProviderPayments/{provider}/{providerPaymentId}";
}

/// <summary>One seller's share of a split payment, as recorded.</summary>
public class PaymentSplitRecord
{
    public string SellerOrganizationId { get; set; } = string.Empty;
    public string AccountId { get; set; } = string.Empty;
    public Money Amount { get; set; } = Money.Zero("EUR");
    public Money? ApplicationFee { get; set; }
    /// <summary>The provider's transfer or route id, once made.</summary>
    public string? TransferId { get; set; }
}

/// <summary>A refund of a <see cref="ProviderPayment"/>.</summary>
public class ProviderPaymentRefund
{
    /// <summary>Our reference (refund, credit note, dispute): also the store-side idempotency key.</summary>
    public string Reference { get; set; } = string.Empty;

    public string? ProviderRefundId { get; set; }

    public Money Amount { get; set; } = Money.Zero("EUR");

    public ProviderRefundStatus Status { get; set; }

    public RefundFailureReason? FailureReason { get; set; }

    public string? FailureMessage { get; set; }

    public string IdempotencyKey { get; set; } = string.Empty;

    /// <summary>Whether the platform's fee was given back with it (fee ledger).</summary>
    public bool ApplicationFeeRefunded { get; set; }

    public DateTime RequestedAtUtc { get; set; }

    public DateTime UpdatedAtUtc { get; set; }
}

/// <summary>
/// A seller's linked provider account (D5, D21; spike S6): the account id, its verification state as the provider
/// last reported it, and for OAuth providers the encrypted tokens. Id <c>SellerPaymentAccounts/{provider}/{organizationId}</c>:
/// one account per seller and provider.
/// </summary>
public class SellerPaymentAccount
{
    public string? Id { get; set; }

    public string SellerOrganizationId { get; set; } = string.Empty;

    public string Provider { get; set; } = string.Empty;

    /// <summary>Stripe <c>acct_…</c>, Mollie <c>org_…</c>; null until onboarding created or linked one.</summary>
    public string? AccountId { get; set; }

    /// <summary>Mollie's website profile (<c>pfl_…</c>), which OAuth payment creation needs.</summary>
    public string? ProfileId { get; set; }

    public KycStatus Kyc { get; set; } = KycStatus.NotStarted;

    public bool ChargesEnabled { get; set; }

    public bool PayoutsEnabled { get; set; }

    public List<string> Requirements { get; set; } = [];

    public string? DisabledReason { get; set; }

    public string? AccountName { get; set; }

    /// <summary>The OAuth tokens, encrypted with ASP.NET Core Data Protection. Never an attribute.</summary>
    public string? ProtectedTokens { get; set; }

    public DateTime? AccessTokenExpiresAtUtc { get; set; }

    /// <summary>SHA-256 of the pending onboarding's <c>state</c> value (never the value itself).</summary>
    public string? PendingStateHash { get; set; }

    public DateTime? PendingStateExpiresAtUtc { get; set; }

    /// <summary>The user who started the link.</summary>
    public string? LinkedBy { get; set; }

    public DateTime? LinkedAtUtc { get; set; }

    public DateTime UpdatedAtUtc { get; set; }

    public static string IdFor(string provider, string organizationId) => $"SellerPaymentAccounts/{provider}/{organizationId}";
}

/// <summary>
/// One dispute or chargeback (GB1). Id <c>PaymentDisputes/{provider}/{providerDisputeId}</c>. The seller's
/// <c>finance</c> members see it, download or submit its evidence pack, and may accept it (a Mollie chargeback has no
/// decision by the provider; accepting it closes it as lost).
/// </summary>
public class PaymentDispute
{
    public string? Id { get; set; }

    public string Provider { get; set; } = string.Empty;

    public string ProviderDisputeId { get; set; } = string.Empty;

    /// <summary>The disputed payment's provider id.</summary>
    public string ProviderPaymentId { get; set; } = string.Empty;

    /// <summary>Our <see cref="ProviderPayment"/> document.</summary>
    public string? PaymentId { get; set; }

    /// <summary>Our order reference.</summary>
    public string? Reference { get; set; }

    public string? SellerOrganizationId { get; set; }

    public string? AccountId { get; set; }

    public Money Amount { get; set; } = Money.Zero("EUR");

    public Money? Fee { get; set; }

    public DisputeStatus Status { get; set; } = DisputeStatus.NeedsResponse;

    public string? Reason { get; set; }

    public string? ReasonText { get; set; }

    public DateTime? EvidenceDueByUtc { get; set; }

    public bool FundsWithdrawn { get; set; }

    public bool FundsReinstated { get; set; }

    public DateTime OpenedAtUtc { get; set; }

    public DateTime? ClosedAtUtc { get; set; }

    public DateTime? EvidenceSubmittedAtUtc { get; set; }

    public string? EvidenceSubmittedBy { get; set; }

    public DateTime? SellerNotifiedAtUtc { get; set; }

    /// <summary>Who accepted the dispute as lost (a Mollie chargeback), or <c>system:finality</c> after the finality window.</summary>
    public string? AcceptedBy { get; set; }

    public DateTime UpdatedAtUtc { get; set; }

    public static string IdFor(string provider, string providerDisputeId) => $"PaymentDisputes/{provider}/{providerDisputeId}";

    /// <summary>Whether the dispute is decided.</summary>
    public bool IsClosed => Status is DisputeStatus.Won or DisputeStatus.Lost or DisputeStatus.WarningClosed;
}

/// <summary>What a platform-fee ledger entry records (GB4).</summary>
public enum PlatformFeeEntryKind
{
    /// <summary>An application fee taken on a paid payment (platform revenue).</summary>
    ApplicationFee,

    /// <summary>An application fee given back with a refund.</summary>
    ApplicationFeeRefund,

    /// <summary>The provider debited a dispute from the seller's account (connected mode).</summary>
    DisputeDebit,

    /// <summary>The provider's dispute fee charged to the seller.</summary>
    DisputeFee,

    /// <summary>A won dispute gave the debited funds back.</summary>
    DisputeReversal,
}

/// <summary>
/// One line of the platform-fee ledger (GB4): every application fee, refund of a fee and dispute debit per seller.
/// Append-only: written by the payments package under a natural id (<c>PlatformFeeEntries/{kind}/{provider}/{source}</c>),
/// so a redelivered webhook cannot book an entry twice; only <c>QueryRead</c> is granted.
/// </summary>
public class PlatformFeeEntry
{
    public string? Id { get; set; }

    public string? SellerOrganizationId { get; set; }

    public string Provider { get; set; } = string.Empty;

    public PlatformFeeEntryKind Kind { get; set; }

    /// <summary>Always positive; <see cref="Kind"/> says the direction.</summary>
    public Money Amount { get; set; } = Money.Zero("EUR");

    /// <summary>The provider resource it books (payment, refund, dispute).</summary>
    public string SourceId { get; set; } = string.Empty;

    /// <summary>Our order reference.</summary>
    public string? Reference { get; set; }

    public DateTime OccurredAtUtc { get; set; }

    public DateTime RecordedAtUtc { get; set; }

    public static string IdFor(PlatformFeeEntryKind kind, string provider, string sourceId) => $"PlatformFeeEntries/{kind}/{provider}/{sourceId}";

    /// <summary>The entry's effect on the platform's fee revenue: + for a fee, − for a fee refund, 0 for seller debits.</summary>
    public long SignedMinorUnits => Kind switch
    {
        PlatformFeeEntryKind.ApplicationFee => Amount.ToMinorUnits(),
        PlatformFeeEntryKind.ApplicationFeeRefund => -Amount.ToMinorUnits(),
        _ => 0,
    };
}

/// <summary>How a settlement line matched our records (GB4).</summary>
public enum SettlementMatch
{
    /// <summary>Matched a payment, refund, dispute or fee with the same amount.</summary>
    Matched,

    /// <summary>A line with nothing of ours to match (provider costs, the payout itself).</summary>
    NotApplicable,

    /// <summary>No record of ours names its source. In the reconciliation queue.</summary>
    Unmatched,

    /// <summary>Matched a record, but the amounts differ. In the reconciliation queue.</summary>
    AmountMismatch,
}

/// <summary>One imported settlement line (GB4). Id <c>SettlementLines/{provider}/{lineId}</c>: imported once.</summary>
public class SettlementLine
{
    public string? Id { get; set; }

    public string Provider { get; set; } = string.Empty;

    /// <summary>The account the settlement belongs to; null for the platform account.</summary>
    public string? AccountId { get; set; }

    public string? SellerOrganizationId { get; set; }

    public string SettlementId { get; set; } = string.Empty;

    public string? SettlementReference { get; set; }

    public string LineId { get; set; } = string.Empty;

    public SettlementLineKind Kind { get; set; }

    public string? SourceId { get; set; }

    public Money Gross { get; set; } = Money.Zero("EUR");

    public Money? Fee { get; set; }

    public Money Net { get; set; } = Money.Zero("EUR");

    public DateTime OccurredAtUtc { get; set; }

    public string? Description { get; set; }

    public SettlementMatch Match { get; set; }

    /// <summary>The document it matched (<see cref="ProviderPayment"/>, <see cref="PaymentDispute"/>, <see cref="PlatformFeeEntry"/>).</summary>
    public string? MatchedDocumentId { get; set; }

    /// <summary>What our record says the amount should be, for a mismatch.</summary>
    public Money? ExpectedAmount { get; set; }

    public DateTime ImportedAtUtc { get; set; }

    public static string IdFor(string provider, string lineId) => $"SettlementLines/{provider}/{lineId}";
}

/// <summary>Why something is in the reconciliation queue.</summary>
public enum ReconciliationProblem
{
    /// <summary>A settlement line names nothing we know.</summary>
    Unmatched,

    /// <summary>A settlement line's amount differs from our record.</summary>
    AmountMismatch,

    /// <summary>A refund failed (insufficient balance on a seller's account, D20, or another reason); someone must act.</summary>
    RefundFailed,
}

public enum ReconciliationStatus
{
    Open,
    Resolved,
}

/// <summary>
/// An item of the reconciliation queue for the <c>finance</c> role (GB4): an unmatched or mismatched settlement line,
/// or a failed refund. Resolved with a note through the <c>ResolveReconciliation</c> action. Id
/// <c>ReconciliationItems/{provider}/{key}</c>.
/// </summary>
public class ReconciliationItem
{
    public string? Id { get; set; }

    public string Provider { get; set; } = string.Empty;

    public string? SellerOrganizationId { get; set; }

    public ReconciliationProblem Problem { get; set; }

    public ReconciliationStatus Status { get; set; } = ReconciliationStatus.Open;

    /// <summary>A one-line description for the queue.</summary>
    public string Summary { get; set; } = string.Empty;

    public string? SettlementId { get; set; }

    public string? SettlementLineId { get; set; }

    /// <summary>The provider resource concerned.</summary>
    public string? SourceId { get; set; }

    /// <summary>Our document concerned (payment, dispute).</summary>
    public string? DocumentId { get; set; }

    public Money? Amount { get; set; }

    public Money? ExpectedAmount { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public string? ResolvedBy { get; set; }

    public DateTime? ResolvedAtUtc { get; set; }

    public string? ResolutionNote { get; set; }

    public static string IdFor(string provider, string key) => $"ReconciliationItems/{provider}/{key}";
}

/// <summary>Where the settlement import of one account stands. Id <c>SettlementImportCursors/{provider}/{account or platform}</c>.</summary>
public class SettlementImportCursor
{
    public string? Id { get; set; }

    /// <summary>The newest imported settlement's creation time; the next import asks for later ones.</summary>
    public DateTime LastCreatedAtUtc { get; set; }

    public DateTime UpdatedAtUtc { get; set; }

    public static string IdFor(string provider, string? accountId) => $"SettlementImportCursors/{provider}/{accountId ?? "platform"}";
}
