using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Builder;

namespace MintPlayer.Spark.Commerce.Payments;

/// <summary>
/// <c>Spark:Commerce:Payments</c> (commerce PRD §4.5). The merchant mode itself is Commerce's
/// (<c>Spark:Commerce:Merchant</c>, D5). Provider credentials live under each adapter's own section
/// (<c>Spark:Commerce:Payments:Mollie</c>, <c>…:Stripe</c>) and come from configuration or user-secrets only.
/// </summary>
/// <example>
/// <code>
/// "Spark": { "Commerce": { "Payments": {
///   "Provider": "mollie",
///   "PlatformFee": { "PercentBasisPoints": 500, "Fixed": { "EUR": 0.25 } },
///   "PlatformOrganizationId": "Organizations/platform",
///   "Methods": { "mollie": { "paypal": { "Enabled": false } } }
/// } } }
/// </code>
/// </example>
public sealed class SparkCommercePaymentsOptions : ISparkConfigurationSection
{
    public static string SectionName => "Spark:Commerce:Payments";

    /// <summary>The provider used when a call names none. Required when more than one adapter is registered.</summary>
    public string? Provider { get; set; }

    /// <summary>The platform's application fee in <see cref="MerchantMode.ConnectedSellers"/> mode (D5); none by default.
    /// Checkout may pass its own per payment.</summary>
    public PlatformFeeOptions PlatformFee { get; set; } = new();

    /// <summary>
    /// The organization whose <c>finance</c> members (and Admins) work the platform-wide queues: every seller's
    /// disputes, the fee ledger, the reconciliation queue. Without it, only each seller's own members see their rows.
    /// </summary>
    public string? PlatformOrganizationId { get; set; }

    /// <summary>Overrides of the method catalog, per provider, then per method id.</summary>
    public Dictionary<string, Dictionary<string, PaymentMethodOverride>> Methods { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The base URL webhooks and redirects are built on; null for the public base URL Spark resolves.</summary>
    public string? PublicBaseUrl { get; set; }

    /// <summary>Connect onboarding (D5, D21).</summary>
    public ConnectOnboardingOptions Connect { get; set; } = new();

    /// <summary>Disputes (GB1).</summary>
    public DisputeOptions Disputes { get; set; } = new();

    /// <summary>Settlement import and reconciliation (GB4).</summary>
    public SettlementOptions Settlements { get; set; } = new();
}

/// <summary>The platform fee (D5): a percentage of the payment plus a fixed amount per currency, both optional.</summary>
public sealed class PlatformFeeOptions
{
    /// <summary>Percentage in basis points (500 = 5 %), of the payment amount.</summary>
    public int PercentBasisPoints { get; set; }

    /// <summary>A fixed amount per ISO 4217 currency, in major units.</summary>
    public Dictionary<string, decimal> Fixed { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The fee for <paramref name="amount"/>: <c>RoundDiv(amount × bp, 10 000) + fixed</c>, never more than the amount;
    /// null when it is zero.
    /// </summary>
    public Money? For(Money amount)
    {
        var minor = Money.RoundDiv(amount.ToMinorUnits() * (long)PercentBasisPoints, 10_000L);
        if (Fixed.TryGetValue(amount.Currency, out var fixedMajor))
            minor += new Money(fixedMajor, amount.Currency).ToMinorUnits();
        minor = Math.Clamp(minor, 0, amount.ToMinorUnits());
        return minor == 0 ? null : Money.FromMinorUnits(minor, amount.Currency);
    }
}

/// <summary>Connect onboarding.</summary>
public sealed class ConnectOnboardingOptions
{
    /// <summary>How long an onboarding <c>state</c> stays redeemable. Default 30 minutes.</summary>
    public TimeSpan StateLifetime { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>The app-relative page the seller lands on after the provider's onboarding. Default <c>/</c>.</summary>
    public string ReturnPath { get; set; } = "/";

    /// <summary>How often linked accounts that are not verified yet are re-read (Mollie sends no onboarding webhook). Every 15 minutes by default.</summary>
    public string KycRefreshSchedule { get; set; } = "*/15 * * * *";
}

/// <summary>Disputes (GB1).</summary>
public sealed class DisputeOptions
{
    /// <summary>
    /// A Mollie chargeback has no decision by the provider: it is reversed or it stays. One not reversed after this long
    /// is closed as lost (I4 then releases, a credit note follows). Default 120 days, the usual card-scheme window for
    /// the issuer's side of a chargeback cycle; a seller may accept it as lost earlier. ⚠️ An owner decision to confirm:
    /// Mollie's public docs state no such deadline.
    /// </summary>
    public TimeSpan ChargebackFinalAfter { get; set; } = TimeSpan.FromDays(120);

    /// <summary>The organization role told of a dispute, beside the seller's Admins and Owners. Default <c>finance</c>.</summary>
    public string NotifyRole { get; set; } = "finance";

    /// <summary>The fallback mail template (no <see cref="IPaymentNotifier"/> registered).</summary>
    public string MailTemplate { get; set; } = "SparkPayments/DisputeOpened";

    /// <summary>The app-relative page of a dispute, <c>{0}</c> being its id. Used as the notice's link.</summary>
    public string UrlFormat { get; set; } = "/po/payment-dispute/{0}";
}

/// <summary>Settlement import (GB4).</summary>
public sealed class SettlementOptions
{
    /// <summary>When settlements are imported. Hourly by default.</summary>
    public string ImportSchedule { get; set; } = "17 * * * *";

    /// <summary>Also import connected sellers' settlements (their reports, §4.19). Default <c>true</c>.</summary>
    public bool IncludeConnectedAccounts { get; set; } = true;

    /// <summary>How far back the first import of an account reaches. Default 35 days.</summary>
    public TimeSpan FirstImportLookback { get; set; } = TimeSpan.FromDays(35);
}
