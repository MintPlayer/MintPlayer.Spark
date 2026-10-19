using MintPlayer.Spark.Abstractions.Builder;

namespace MintPlayer.Spark.Commerce.Checkout;

/// <summary>
/// <c>Spark:Commerce:Checkout</c> (commerce PRD §4.4). The basket's channel rules (<c>Basket:MixChannels</c>,
/// <c>Basket:MixSellers</c>, <c>Basket:OnLineFailure</c>) and the channels themselves stay under <c>Spark:Commerce</c>;
/// this section holds what only checkout reads. Code given to <c>AddCommerceCheckout(o => …)</c> runs after the section.
/// </summary>
public sealed class SparkCommerceCheckoutOptions : ISparkConfigurationSection
{
    public static string SectionName => "Spark:Commerce:Checkout";

    /// <summary>D3: added to a method's provider expiry for the PaymentPending deadline (still capped by <c>MaxPaymentWindow</c>). Default 2 minutes.</summary>
    public TimeSpan PaymentGrace { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>How long an OnPayment line's claim is held between "claim" and "convert" at confirmation (D1). Default 1 minute.</summary>
    public TimeSpan ConfirmationHold { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// GA2: the legal-document kinds the buyer must accept at the last step (the versions in force for every seller of
    /// the basket, else the platform's). Default: terms of sale. The privacy policy and withdrawal information are
    /// linked (information duties), not accepted.
    /// </summary>
    public List<string> RequiredAcceptance { get; set; } = [LegalDocumentKinds.TermsOfSale];

    /// <summary>GA2: the kinds linked at the last step and attached to the confirmation (durable medium, CRD Art. 8(7)).</summary>
    public List<string> LinkedDocuments { get; set; } = [LegalDocumentKinds.TermsOfSale, LegalDocumentKinds.PrivacyPolicy, LegalDocumentKinds.WithdrawalInformation];

    /// <summary>Checkout's own per-channel settings, by channel name (the channel itself is <c>Spark:Commerce:Channels</c>).</summary>
    public Dictionary<string, CheckoutChannelOptions> Channels { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Where the buyer lands after paying: app-relative, <c>{number}</c> is replaced. Default <c>/checkout/orders/{number}</c>.</summary>
    public string OrderPagePath { get; set; } = "/checkout/orders/{number}";

    /// <summary>The confirmation mail's template (GA2 durable confirmation).</summary>
    public string ConfirmationMailTemplate { get; set; } = "SparkCheckout/OrderConfirmation";

    /// <summary>GB7: the payment-link mail of a phone order.</summary>
    public string PaymentLinkMailTemplate { get; set; } = "SparkCheckout/PaymentLink";

    /// <summary>GB7: the organization role that may create back-office orders (declared by <c>AddCommerceCheckout()</c>).</summary>
    public string BackOfficeRole { get; set; } = "box-office";

    /// <summary>GB7: the organization role that may mark a manual order paid (audited). Default <c>finance</c>.</summary>
    public string MarkPaidRole { get; set; } = "finance";

    /// <summary>The PaymentPending sweep (cron, six fields = seconds). Default every 30 seconds. Read at registration.</summary>
    public string SweepSchedule { get; set; } = "*/30 * * * * *";

    /// <summary>
    /// S21: how a line's net and VAT are recomputed from its adjusted total (a discount, a sale price). Default
    /// <see cref="CheckoutVatRounding.PerLine"/>; Invoicing re-splits per rate group on its documents either way.
    /// </summary>
    public CheckoutVatRounding VatRounding { get; set; } = CheckoutVatRounding.PerLine;

    public CheckoutAbandonedBasketOptions AbandonedBaskets { get; set; } = new();

    public CheckoutDonationOptions Donation { get; set; } = new();

    public CheckoutInsuranceOptions Insurance { get; set; } = new();

    /// <summary>M9: refunds, cancellations, withdrawal (GA1), guarantee claims (GA8) and mass refunds (<c>Spark:Commerce:Checkout:Refunds</c>).</summary>
    public Refunds.CheckoutRefundOptions Refunds { get; set; } = new();

    internal CheckoutChannelOptions Channel(string name)
        => Channels.TryGetValue(name, out var options) ? options : CheckoutChannelOptions.Default;
}

/// <summary>Checkout's settings for one channel.</summary>
public sealed class CheckoutChannelOptions
{
    internal static readonly CheckoutChannelOptions Default = new();

    /// <summary>Payment methods this channel never offers (method ids of the catalog).</summary>
    public List<string> ExcludedMethods { get; set; } = [];

    /// <summary>
    /// Authorize-then-capture: the order is captured once every line converted, so a race loser or a refused card
    /// fingerprint is voided, not refunded. Applies when every offered method supports authorization.
    /// </summary>
    public bool AuthorizeThenCapture { get; set; }

    /// <summary>GC1: opt in to the abandoned-basket reminder for baskets with lines of this channel. Default off.</summary>
    public bool AbandonedBasketReminder { get; set; }
}

/// <summary>GC1: the abandoned-basket reminder.</summary>
public sealed class CheckoutAbandonedBasketOptions
{
    /// <summary>How long a basket stays untouched before the one reminder. Default 4 hours.</summary>
    public TimeSpan Delay { get; set; } = TimeSpan.FromHours(4);

    /// <summary>Baskets untouched longer than this get no reminder. Default 7 days.</summary>
    public TimeSpan MaxAge { get; set; } = TimeSpan.FromDays(7);

    /// <summary>The job's schedule (six fields). Default every 10 minutes. Read at registration.</summary>
    public string Schedule { get; set; } = "0 */10 * * * *";

    public string MailTemplate { get; set; } = "SparkCheckout/AbandonedBasket";

    /// <summary>The MailManager unsubscribe stream the reminder belongs to.</summary>
    public string Stream { get; set; } = "marketing-mail";

    /// <summary>Baskets one run handles at most. Default 200.</summary>
    public int BatchSize { get; set; } = 200;
}

/// <summary>GC3: donations and round-up.</summary>
public sealed class CheckoutDonationOptions
{
    public bool Enabled { get; set; }

    /// <summary>The beneficiary's name shown to the buyer.</summary>
    public string? Beneficiary { get; set; }

    /// <summary>The beneficiary organization, if it is one of the app's organizations (reports).</summary>
    public string? BeneficiaryOrganizationId { get; set; }

    /// <summary>Fixed amounts offered, per currency (<c>"EUR": [1, 2, 5]</c>).</summary>
    public Dictionary<string, List<decimal>> FixedAmounts { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Offer rounding the total up to the next whole currency unit.</summary>
    public bool RoundUp { get; set; } = true;

    /// <summary>The VAT category of the donation line (host configurable). Default <c>outside-scope</c>.</summary>
    public string VatCategory { get; set; } = "outside-scope";

    /// <summary>The largest donation accepted, per currency (a guard against typos). Default 1000 of any currency.</summary>
    public decimal MaxAmount { get; set; } = 1000m;
}

/// <summary>GC4: ticket insurance.</summary>
public sealed class CheckoutInsuranceOptions
{
    /// <summary>The <see cref="IInsuranceProvider.Name"/> to use when several are registered; null with one uses it, with none offers nothing.</summary>
    public string? Provider { get; set; }

    /// <summary>The channels whose lines are offered cover; empty means every channel.</summary>
    public List<string> Channels { get; set; } = [];
}
