using MintPlayer.Spark.Abstractions.Builder;

namespace MintPlayer.Spark.Commerce.Payments.Mollie;

/// <summary>
/// <c>Spark:Commerce:Payments:Mollie</c>. The credentials come from configuration or user-secrets only; they are read
/// per call (<c>IOptionsMonitor</c>), so a rotated key takes effect without a restart, and never logged.
/// </summary>
public sealed class MolliePaymentsOptions : ISparkConfigurationSection
{
    public static string SectionName => "Spark:Commerce:Payments:Mollie";

    /// <summary>The platform's API key (<c>live_…</c> or <c>test_…</c>). Used for the platform's own payments
    /// (<see cref="MerchantMode.Platform"/> mode).</summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// An organization access token (<c>access_…</c>) of the platform's own organization, for its settlements: Mollie's
    /// Settlements API takes an organization token or OAuth, not an API key. Without it the platform's own settlements are
    /// not imported (connected sellers' are, with their OAuth tokens).
    /// </summary>
    public string? OrganizationToken { get; set; }

    /// <summary>The Mollie Connect app's client id (<c>app_…</c>), for <see cref="MerchantMode.ConnectedSellers"/> mode.</summary>
    public string? ClientId { get; set; }

    /// <summary>The Mollie Connect app's client secret.</summary>
    public string? ClientSecret { get; set; }

    /// <summary>
    /// Calls made with a seller's OAuth token go to test mode (<c>testmode=true</c>). With an API key, the key's prefix
    /// decides and this is ignored.
    /// </summary>
    public bool TestMode { get; set; }

    /// <summary>The OAuth scopes asked of a seller. The default is what payments, refunds, onboarding status and settlements need.</summary>
    public string Scopes { get; set; } = "payments.read payments.write refunds.read refunds.write organizations.read profiles.read onboarding.read settlements.read balances.read";

    /// <summary>
    /// Split payments (D40) through Mollie's routing (<c>routing[]</c> on the payment, to connected organizations).
    /// Off by default: it needs Mollie to enable "split payments" on the platform's account, and it must be confirmed
    /// against the host's Mollie contract (D37). On, the card, iDEAL and Bancontact methods report <c>supportsSplit</c>.
    /// </summary>
    public bool SplitPayments { get; set; }

    /// <summary>The API base; overridable for tests only.</summary>
    public string ApiBaseUrl { get; set; } = "https://api.mollie.com/v2/";

    /// <summary>The OAuth authorize page.</summary>
    public string AuthorizeUrl { get; set; } = "https://my.mollie.com/oauth2/authorize";

    /// <summary>The OAuth token endpoint.</summary>
    public string TokenUrl { get; set; } = "https://api.mollie.com/oauth2/tokens";
}
