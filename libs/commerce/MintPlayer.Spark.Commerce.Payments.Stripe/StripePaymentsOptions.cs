using MintPlayer.Spark.Abstractions.Builder;

namespace MintPlayer.Spark.Commerce.Payments.Stripe;

/// <summary>
/// <c>Spark:Commerce:Payments:Stripe</c>. Keys come from configuration or user-secrets only, are read per call, and are
/// never logged.
/// </summary>
public sealed class StripePaymentsOptions : ISparkConfigurationSection
{
    public static string SectionName => "Spark:Commerce:Payments:Stripe";

    /// <summary>The platform's secret key (<c>sk_live_…</c>, <c>sk_test_…</c>, or a restricted <c>rk_…</c>).</summary>
    public string? SecretKey { get; set; }

    /// <summary>The signing secret (<c>whsec_…</c>) of the endpoint <c>/spark/webhooks/payments-stripe</c> (the platform's own events).</summary>
    public string? WebhookSecret { get; set; }

    /// <summary>The signing secret of the Connect endpoint <c>/spark/webhooks/payments-stripe-connect</c> (connected accounts' events).</summary>
    public string? ConnectWebhookSecret { get; set; }

    /// <summary>How far a webhook's signed timestamp may be from now. Default 5 minutes (Stripe's libraries' default).</summary>
    public TimeSpan WebhookTolerance { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The connected-account type Connect creates: <c>standard</c> (default; the seller has a full Stripe account and is
    /// debited for its own refunds and disputes, which is what D5 wants) or <c>express</c>.
    /// </summary>
    public string ConnectAccountType { get; set; } = "standard";

    /// <summary>The country of new connected accounts (ISO 3166-1 alpha-2); null lets the seller choose in onboarding.</summary>
    public string? ConnectCountry { get; set; }

    /// <summary>
    /// Split payments (D40) through separate charges and transfers: the buyer pays the platform, which then transfers each
    /// seller's share (<c>source_transaction</c>). Off by default: the money passes through the platform's balance, so the
    /// platform is the merchant of record for a split order, the opt-in exception to D5 (D41): a split is only made when
    /// <c>Spark:Commerce:Basket:MixSellers</c> and <c>AcknowledgeSplitMerchantOfRecord</c> are on as well.
    /// </summary>
    public bool SeparateChargesAndTransfers { get; set; }

    /// <summary>The <c>Stripe-Version</c> pinned on every call; null for the account's default. Pin it in production.</summary>
    public string? ApiVersion { get; set; }

    /// <summary>The API base; overridable for tests only.</summary>
    public string ApiBaseUrl { get; set; } = "https://api.stripe.com/v1/";
}
