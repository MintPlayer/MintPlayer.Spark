using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Webhooks;

namespace MintPlayer.Spark.Commerce.Payments;

/// <summary>
/// One payment provider (commerce PRD §4.5, D6): Mollie, Stripe, or the in-process test provider. An adapter package
/// registers one implementation with <see cref="SparkCommercePaymentsExtensions.AddPaymentProvider{TProvider}"/>; the
/// rest of the suite only talks to <see cref="ISparkPayments"/>, which picks the provider, the merchant context and the
/// idempotency key and records what the provider answered.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every call is retry-safe.</b> Each mutating call carries an idempotency key
/// (<see cref="PaymentIdempotencyKeys"/>); an adapter sends it to the provider (Stripe and Mollie both honour an
/// <c>Idempotency-Key</c> header), so a retry after a timeout does not create a second payment or refund.
/// </para>
/// <para>
/// <b>Webhooks are the source of truth</b> (research §4.3). Redirects are UX; <see cref="ParseWebhookAsync"/> turns a
/// verified delivery into provider events, which <see cref="ISparkPayments"/> records and hands to the
/// <see cref="IPaymentEventRecipient"/>s (Checkout, Tickets). A fetch-by-id provider (Mollie) fetches the resource here,
/// with the merchant credentials the payment was created with.
/// </para>
/// <para>
/// <b>No country input (GA11).</b> No member of this interface takes the buyer's nationality, residence or card-issuing
/// country, so no adapter can hide a method or refuse a payment on it (Regulation (EU) 2018/302, Art. 5(1)).
/// </para>
/// </remarks>
public interface IPaymentProvider
{
    /// <summary>The provider's name, lower case: <c>mollie</c>, <c>stripe</c>, <c>test</c>. Also its webhook source name's stem.</summary>
    string Name { get; }

    /// <summary>What the provider can do beyond creating payments.</summary>
    PaymentProviderCapabilities Capabilities { get; }

    /// <summary>The method catalog (§4.5): expiry, authorization support, fingerprint timing, split capability.</summary>
    IReadOnlyList<PaymentMethodInfo> Methods { get; }

    /// <summary>Creates a payment and returns where to send the buyer.</summary>
    Task<ProviderPaymentSnapshot> CreatePaymentAsync(CreatePaymentRequest request, CancellationToken cancellationToken);

    /// <summary>The payment as the provider has it now.</summary>
    Task<ProviderPaymentSnapshot> GetPaymentAsync(MerchantContext merchant, string providerPaymentId, CancellationToken cancellationToken);

    /// <summary>
    /// Cancels a payment the buyer has not completed (D3: the buyer cancelled, or the hold died). Mollie cancels an
    /// open payment when it is cancelable; Stripe expires the Checkout Session. A payment that cannot be cancelled any
    /// more answers its current state: the late-payment path then decides (research §4.4).
    /// </summary>
    Task<ProviderPaymentSnapshot> CancelPaymentAsync(MerchantContext merchant, string providerPaymentId, string idempotencyKey, CancellationToken cancellationToken);

    /// <summary>Captures an authorized payment, in full or <paramref name="amount"/> of it (authorize-then-capture, research §4.2).</summary>
    Task<ProviderPaymentSnapshot> CaptureAsync(MerchantContext merchant, string providerPaymentId, Money? amount, string idempotencyKey, CancellationToken cancellationToken);

    /// <summary>Releases an authorization without moving money (the allocation failed after authorization).</summary>
    Task<ProviderPaymentSnapshot> VoidAsync(MerchantContext merchant, string providerPaymentId, string idempotencyKey, CancellationToken cancellationToken);

    /// <summary>
    /// Refunds all or part of a paid payment. In <see cref="MerchantMode.ConnectedSellers"/> mode the refund comes from
    /// the seller's own balance; a refund the provider refuses for lack of balance answers
    /// <see cref="RefundFailureReason.InsufficientBalance"/> rather than throwing (D20).
    /// </summary>
    Task<ProviderRefundSnapshot> RefundAsync(RefundRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Turns one verified webhook delivery into provider events. A signed provider reads the body; a fetch-by-id
    /// provider resolves <see cref="WebhookDelivery.ResourceId"/>'s merchant through <paramref name="merchants"/>
    /// and fetches the resource. An id that is not one of ours answers no events (a forged id is harmless).
    /// </summary>
    Task<IReadOnlyList<PaymentProviderEvent>> ParseWebhookAsync(WebhookDelivery delivery, IPaymentMerchantResolver merchants, CancellationToken cancellationToken);

    /// <summary>
    /// Submits an evidence pack for a dispute (GB1). Stripe accepts evidence by API; a provider that does not (Mollie:
    /// its dashboard only) answers <see cref="DisputeEvidenceSubmission.ManualOnly"/> and the pack is a download.
    /// </summary>
    Task<DisputeEvidenceSubmission> SubmitDisputeEvidenceAsync(MerchantContext merchant, string providerDisputeId, DisputeEvidencePack pack, CancellationToken cancellationToken);
}

/// <summary>What a provider supports beyond creating and refunding payments.</summary>
/// <param name="ConnectOnboarding">Sellers can link their own account (<see cref="IConnectOnboardingProvider"/>).</param>
/// <param name="ApplicationFees">A platform fee can be taken from a connected seller's payment.</param>
/// <param name="SupportsSplit">One payment can be split across several sellers (D40). Reported to Commerce, which
/// refuses <c>Basket:MixSellers</c> without it.</param>
/// <param name="DisputeEvidenceByApi">Dispute evidence is submitted by API (Stripe); otherwise it is a download (Mollie).</param>
/// <param name="Settlements">The provider's settlement or payout report can be imported (<see cref="ISettlementSource"/>).</param>
public sealed record PaymentProviderCapabilities(
    bool ConnectOnboarding,
    bool ApplicationFees,
    bool SupportsSplit,
    bool DisputeEvidenceByApi,
    bool Settlements);

/// <summary>Whether money moves when the buyer pays, or only after an explicit capture.</summary>
public enum PaymentCaptureMode
{
    /// <summary>Money moves when the buyer completes the payment. Default.</summary>
    Automatic,

    /// <summary>The payment is only authorized; <see cref="IPaymentProvider.CaptureAsync"/> moves the money and
    /// <see cref="IPaymentProvider.VoidAsync"/> releases it (research §4.2). Only for methods with
    /// <see cref="PaymentMethodInfo.SupportsAuthorization"/>.</summary>
    Manual,
}

/// <summary>A new payment, as Checkout asks for it through <see cref="ISparkPayments.CreatePaymentAsync"/>.</summary>
public sealed record CreatePaymentRequest
{
    /// <summary>Whose account takes the money, with the credentials to reach it.</summary>
    public required MerchantContext Merchant { get; init; }

    /// <summary>Our reference: the order (or order + attempt) the payment is for. Written into the provider's metadata.</summary>
    public required string Reference { get; init; }

    /// <summary>The amount the buyer pays.</summary>
    public required Money Amount { get; init; }

    /// <summary>The description the buyer sees at the provider and on their statement (providers truncate it).</summary>
    public required string Description { get; init; }

    /// <summary>The method id from the catalog (<c>ideal</c>, <c>card</c>, …); null lets the buyer choose at the provider
    /// among <see cref="AllowedMethods"/>.</summary>
    public string? Method { get; init; }

    /// <summary>The methods the buyer may choose from when <see cref="Method"/> is null (the D3 cap already applied).</summary>
    public IReadOnlyList<string> AllowedMethods { get; init; } = [];

    /// <summary>Automatic, or authorize-then-capture.</summary>
    public PaymentCaptureMode Capture { get; init; } = PaymentCaptureMode.Automatic;

    /// <summary>
    /// When the payment should stop being payable (D3: the hold's PaymentPending deadline). A provider that can set an
    /// expiry uses it, clamped to its own bounds (Stripe Checkout: 30 min to 24 h); every provider is also cancelled
    /// actively when the hold dies.
    /// </summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>Where the provider sends the buyer back (absolute).</summary>
    public required string RedirectUrl { get; init; }

    /// <summary>Where the provider sends a buyer who cancels (absolute); null for <see cref="RedirectUrl"/>.</summary>
    public string? CancelUrl { get; init; }

    /// <summary>The webhook URL (absolute), for providers that take one per payment (Mollie).</summary>
    public string? WebhookUrl { get; init; }

    /// <summary>The platform's fee in <see cref="MerchantMode.ConnectedSellers"/> mode (D5); null for none.</summary>
    public Money? ApplicationFee { get; init; }

    /// <summary>A split across several connected sellers (D40); empty for a single merchant.</summary>
    public IReadOnlyList<PaymentSplit> Splits { get; init; } = [];

    /// <summary>The key that makes a retried create return the same payment (<see cref="PaymentIdempotencyKeys"/>).</summary>
    public required string IdempotencyKey { get; init; }

    /// <summary>The buyer's locale for the provider's pages (<c>nl-BE</c>); null for the provider's choice.</summary>
    public string? Locale { get; init; }

    /// <summary>The buyer's e-mail, when the provider asks for it on its page.</summary>
    public string? CustomerEmail { get; init; }

    /// <summary>Further metadata stored with the payment at the provider (keys and values are short strings).</summary>
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();
}

/// <summary>One seller's share of a split payment (D40).</summary>
/// <param name="SellerOrganizationId">The seller organization.</param>
/// <param name="AccountId">The seller's connected account at the provider.</param>
/// <param name="Amount">What the seller receives (before the provider's own fees).</param>
/// <param name="ApplicationFee">The platform's fee on this share; null for none.</param>
public sealed record PaymentSplit(string SellerOrganizationId, string AccountId, Money Amount, Money? ApplicationFee);

/// <summary>A unified payment status (Mollie's statuses; Stripe's Checkout Session and PaymentIntent states mapped onto them).</summary>
public enum ProviderPaymentStatus
{
    /// <summary>Created; the buyer has not finished. Mollie <c>open</c>; Stripe session <c>open</c> / PI <c>requires_*</c>.</summary>
    Open,

    /// <summary>The buyer finished; the provider waits for the method's confirmation. Mollie <c>pending</c>; Stripe <c>processing</c>.</summary>
    Pending,

    /// <summary>Authorized, waiting for capture. Mollie <c>authorized</c>; Stripe <c>requires_capture</c>.</summary>
    Authorized,

    /// <summary>Paid. Mollie <c>paid</c>; Stripe <c>succeeded</c> / session <c>complete</c> + <c>paid</c>.</summary>
    Paid,

    /// <summary>Cancelled (by the buyer, by us, or an authorization voided). Final.</summary>
    Canceled,

    /// <summary>Expired at the provider. Final.</summary>
    Expired,

    /// <summary>Failed. Final.</summary>
    Failed,
}

/// <summary>When a method's card or account fingerprint becomes known (§4.5, research §4.5; D29 linked limits).</summary>
public enum FingerprintTiming
{
    /// <summary>The method yields no payer fingerprint.</summary>
    None,

    /// <summary>Before the charge is confirmed (Stripe <c>card.fingerprint</c>): an over-limit payer is refused before money moves.</summary>
    BeforeConfirm,

    /// <summary>Only after payment (Mollie <c>details.cardFingerprint</c>): an over-limit order is voided or refunded.</summary>
    AfterPayment,
}

/// <summary>The payment as the provider reported it.</summary>
public sealed record ProviderPaymentSnapshot
{
    public required string Provider { get; init; }

    /// <summary>The provider's id: Mollie <c>tr_…</c>; Stripe the Checkout Session <c>cs_…</c>.</summary>
    public required string ProviderPaymentId { get; init; }

    public required ProviderPaymentStatus Status { get; init; }

    public required Money Amount { get; init; }

    /// <summary>What has been refunded so far, when the provider reports it.</summary>
    public Money? AmountRefunded { get; init; }

    /// <summary>Where to send the buyer to pay; null once the payment is no longer open.</summary>
    public string? CheckoutUrl { get; init; }

    /// <summary>When the provider will expire the payment, when it says.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>The method used (catalog id), once known.</summary>
    public string? Method { get; init; }

    /// <summary>Our reference, read back from the provider's metadata.</summary>
    public string? Reference { get; init; }

    /// <summary>A secondary provider id (Stripe's PaymentIntent <c>pi_…</c> and charge <c>ch_…</c>); used to match settlements and disputes.</summary>
    public IReadOnlyList<string> RelatedIds { get; init; } = [];

    /// <summary>The payer fingerprint, when the method has one (D29). Never shown; used as a limit key.</summary>
    public string? PayerFingerprint { get; init; }

    /// <summary>Whether the card is prepaid, when the provider says (D29: prepaid refusal is method-based, never country-based).</summary>
    public bool? Prepaid { get; init; }

    /// <summary>The application fee the provider recorded on the payment.</summary>
    public Money? ApplicationFee { get; init; }

    /// <summary>When the provider says the payment was paid.</summary>
    public DateTimeOffset? PaidAt { get; init; }

    /// <summary>When the provider last changed the payment, or when the event that carried it was created.</summary>
    public DateTimeOffset? UpdatedAt { get; init; }
}

/// <summary>A refund to make.</summary>
public sealed record RefundRequest
{
    public required MerchantContext Merchant { get; init; }

    public required string ProviderPaymentId { get; init; }

    public required Money Amount { get; init; }

    /// <summary>Our refund reference (the refund, credit note or dispute id); also the store-side idempotency key.</summary>
    public required string Reference { get; init; }

    public required string IdempotencyKey { get; init; }

    public string? Description { get; init; }

    /// <summary>Whether the platform's application fee is refunded pro rata (Stripe <c>refund_application_fee</c>).</summary>
    public bool RefundApplicationFee { get; init; }
}

public enum ProviderRefundStatus
{
    /// <summary>Accepted, not yet paid out (Mollie <c>queued</c>/<c>pending</c>/<c>processing</c>; Stripe <c>pending</c>).</summary>
    Pending,

    /// <summary>Paid out to the buyer.</summary>
    Succeeded,

    /// <summary>Refused or failed; see <see cref="ProviderRefundSnapshot.FailureReason"/>.</summary>
    Failed,

    /// <summary>Cancelled before it was paid out.</summary>
    Canceled,
}

/// <summary>Why a refund failed (D20).</summary>
public enum RefundFailureReason
{
    /// <summary>The merchant's balance cannot cover it (a connected seller's account in particular). Retried after a top-up.</summary>
    InsufficientBalance,

    /// <summary>The buyer's card or account can no longer receive it.</summary>
    DestinationUnavailable,

    /// <summary>The payment is disputed; it cannot be refunded meanwhile.</summary>
    Disputed,

    /// <summary>Anything else; <see cref="ProviderRefundSnapshot.FailureMessage"/> says what.</summary>
    Other,
}

/// <summary>A refund as the provider reported it.</summary>
public sealed record ProviderRefundSnapshot
{
    public required string Provider { get; init; }

    /// <summary>The provider's refund id (Mollie <c>re_…</c>, Stripe <c>re_…</c>); null when the provider refused before creating one.</summary>
    public string? ProviderRefundId { get; init; }

    public required string ProviderPaymentId { get; init; }

    public required ProviderRefundStatus Status { get; init; }

    public required Money Amount { get; init; }

    /// <summary>Our reference, read back from metadata where the provider keeps it.</summary>
    public string? Reference { get; init; }

    public RefundFailureReason? FailureReason { get; init; }

    /// <summary>The provider's own words, for the finance queue. Never shown to buyers.</summary>
    public string? FailureMessage { get; init; }

    public DateTimeOffset? UpdatedAt { get; init; }
}

/// <summary>
/// Implemented by a provider whose split (D40) needs a step after the payment is paid: Stripe's separate charges and
/// transfers make one transfer per seller from the charge (<c>source_transaction</c>). Mollie's split payments route at
/// payment time and need no step. Called by the payments package once a split payment turns Paid, with an idempotency
/// key per seller, until every split has its transfer id.
/// </summary>
public interface IPaymentSplitSettler
{
    /// <summary>Makes the transfer of one split; answers the provider's transfer id.</summary>
    Task<string> TransferAsync(string providerPaymentId, IReadOnlyList<string> relatedIds, PaymentSplit split, string reference, string idempotencyKey, CancellationToken cancellationToken);
}
