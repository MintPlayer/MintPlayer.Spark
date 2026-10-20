using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Commerce.Payments.Models;

namespace MintPlayer.Spark.Commerce.Payments;

/// <summary>
/// The payments facade the rest of the suite uses (commerce PRD §4.5). Checkout orchestrates (when to pay, what a
/// late payment does); this records what the provider did, under our references, idempotently.
/// </summary>
public interface ISparkPayments
{
    /// <summary>The provider a call without a provider name uses.</summary>
    string DefaultProvider { get; }

    /// <summary>The catalog of <paramref name="provider"/> (the default when null), host overrides applied.</summary>
    IReadOnlyList<PaymentMethodInfo> GetMethods(string? provider = null);

    /// <summary>The methods a basket may offer (D3 cap, D40 split). Never filtered by country (GA11).</summary>
    IReadOnlyList<PaymentMethodInfo> GetOfferableMethods(PaymentMethodQuery query, string? provider = null);

    /// <summary>
    /// Creates a payment, or returns the one already created for the same reference and attempt (the idempotency key
    /// is derived from them). In <see cref="MerchantMode.ConnectedSellers"/> mode the seller's account must be verified
    /// and able to take charges; the platform fee is taken from <see cref="SparkCommercePaymentsOptions.PlatformFee"/>
    /// unless the request names one.
    /// </summary>
    /// <exception cref="SparkValidationException">The seller cannot take payments (no linked or verified account).</exception>
    Task<ProviderPayment> CreatePaymentAsync(PaymentCreation creation, CancellationToken cancellationToken = default);

    /// <summary>The recorded payment, refreshed from the provider when <paramref name="refresh"/> is set.</summary>
    Task<ProviderPayment?> GetPaymentAsync(string paymentId, bool refresh = false, CancellationToken cancellationToken = default);

    /// <summary>Cancels a payment that is still open (the buyer cancelled, or the hold died, D3).</summary>
    Task<ProviderPayment> CancelAsync(string paymentId, CancellationToken cancellationToken = default);

    /// <summary>Captures an authorized payment (all, or <paramref name="amount"/>).</summary>
    Task<ProviderPayment> CaptureAsync(string paymentId, Money? amount = null, CancellationToken cancellationToken = default);

    /// <summary>Releases an authorization.</summary>
    Task<ProviderPayment> VoidAsync(string paymentId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Refunds <paramref name="amount"/> of a paid payment under <paramref name="reference"/> (a refund, credit-note or
    /// dispute id). The same reference twice returns the first refund. A refund the provider refuses for lack of balance
    /// is recorded as failed with <see cref="RefundFailureReason.InsufficientBalance"/>, queued for <c>finance</c> and
    /// reported to the recipients; it is never thrown (D20). Retry it later with <paramref name="attempt"/> + 1.
    /// </summary>
    Task<ProviderPaymentRefund> RefundAsync(string paymentId, Money amount, string reference, bool refundApplicationFee = true, int attempt = 0, CancellationToken cancellationToken = default);

    /// <summary>Records provider events (from a webhook or a poll) and tells the recipients. Idempotent.</summary>
    Task ProcessEventsAsync(IReadOnlyList<PaymentProviderEvent> events, CancellationToken cancellationToken = default);

    /// <summary>Assembles the evidence pack of a dispute from every <see cref="IDisputeEvidenceContributor"/> (GB1).</summary>
    Task<DisputeEvidencePack> AssembleEvidenceAsync(string disputeId, CancellationToken cancellationToken = default);

    /// <summary>Submits the evidence pack (Stripe), or answers <see cref="DisputeEvidenceSubmission.ManualOnly"/> (Mollie: download it).</summary>
    Task<DisputeEvidenceSubmission> SubmitEvidenceAsync(string disputeId, string actingUserId, CancellationToken cancellationToken = default);

    /// <summary>The seller accepts a dispute as lost (a Mollie chargeback has no other end). Recipients are told it is lost.</summary>
    Task<PaymentDispute> AcceptDisputeAsync(string disputeId, string actingUserId, CancellationToken cancellationToken = default);
}

/// <summary>A payment Checkout asks for.</summary>
public sealed record PaymentCreation
{
    /// <summary>Our reference (the order id).</summary>
    public required string Reference { get; init; }

    /// <summary>Raise it to create a new provider payment for the same reference on purpose (the first one expired).</summary>
    public int Attempt { get; init; }

    /// <summary>The seller; null for the platform's own sale (and always ignored in <see cref="MerchantMode.Platform"/> mode).</summary>
    public string? SellerOrganizationId { get; init; }

    /// <summary>The provider; null for <see cref="ISparkPayments.DefaultProvider"/>.</summary>
    public string? Provider { get; init; }

    public required Money Amount { get; init; }

    public required string Description { get; init; }

    public string? Method { get; init; }

    public IReadOnlyList<string> AllowedMethods { get; init; } = [];

    public PaymentCaptureMode Capture { get; init; }

    /// <summary>The hold's PaymentPending deadline (D3).</summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>Where the buyer returns, absolute or app-relative (made absolute with the public base URL).</summary>
    public required string RedirectUrl { get; init; }

    public string? CancelUrl { get; init; }

    /// <summary>The platform fee; null to use <see cref="SparkCommercePaymentsOptions.PlatformFee"/> (connected mode only).</summary>
    public Money? ApplicationFee { get; init; }

    /// <summary>A split across sellers (D40). Needs a provider whose methods support it.</summary>
    public IReadOnlyList<PaymentSplitRequest> Splits { get; init; } = [];

    public string? Locale { get; init; }

    public string? CustomerEmail { get; init; }

    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();
}

/// <summary>One seller's share of a split (D40); the account is looked up from the seller's link.</summary>
public sealed record PaymentSplitRequest(string SellerOrganizationId, Money Amount, Money? ApplicationFee = null);

/// <summary>
/// Hears what happened to payments (Checkout: confirm, late payment, refund state, the Disputed state; Tickets: freeze
/// and unfreeze; Invoicing: credit notes). Discovered in an application (<c>[SparkDiscoverable]</c>); a library
/// registers its own. Called after the change is stored, from the webhook queue: a recipient that throws makes the
/// delivery retry, so every method must be idempotent (key on the document id and status).
/// </summary>
[SparkDiscoverable]
public interface IPaymentEventRecipient
{
    /// <summary>A payment's status changed (paid, authorized, expired, failed, cancelled).</summary>
    Task OnPaymentChangedAsync(PaymentChangeNotice notice, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>A refund's status changed; a refund that failed for lack of balance included (D20).</summary>
    Task OnRefundChangedAsync(RefundChangeNotice notice, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>A dispute opened, changed or closed (GB1). Lost: release what is releasable with the dispute id as the key (I4).</summary>
    Task OnDisputeChangedAsync(DisputeChangeNotice notice, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>A seller's linked account changed verification state (D21).</summary>
    Task OnSellerAccountChangedAsync(SellerAccountChangeNotice notice, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>A payment changed from <see cref="PreviousStatus"/>.</summary>
public sealed record PaymentChangeNotice(ProviderPayment Payment, ProviderPaymentStatus PreviousStatus);

/// <summary>A refund of <see cref="Payment"/> changed.</summary>
public sealed record RefundChangeNotice(ProviderPayment Payment, ProviderPaymentRefund Refund, ProviderRefundStatus? PreviousStatus);

/// <summary>A dispute changed from <see cref="PreviousStatus"/> (null when it was just opened).</summary>
public sealed record DisputeChangeNotice(PaymentDispute Dispute, DisputeStatus? PreviousStatus);

/// <summary>A seller account changed from <see cref="PreviousKyc"/>.</summary>
public sealed record SellerAccountChangeNotice(SellerPaymentAccount Account, KycStatus PreviousKyc);

/// <summary>
/// Delivers the seller notice of a dispute (GB1: "the seller is notified, mail + notification"). Discovered in an
/// application (<c>[SparkDiscoverable]</c>); Notifications or the host registers one for in-app, mail and push. With
/// none, Payments mails the seller's Owners, Admins and <c>finance</c> members itself through MailManager.
/// </summary>
[SparkDiscoverable]
public interface IPaymentNotifier
{
    Task NotifyDisputeAsync(PaymentDisputeNotice notice, CancellationToken cancellationToken);
}

/// <summary>One dispute notice.</summary>
/// <param name="Dispute">The dispute.</param>
/// <param name="UserIds">The seller's users to tell.</param>
/// <param name="Url">The dispute's app-relative page.</param>
public sealed record PaymentDisputeNotice(PaymentDispute Dispute, IReadOnlyList<string> UserIds, string Url);
