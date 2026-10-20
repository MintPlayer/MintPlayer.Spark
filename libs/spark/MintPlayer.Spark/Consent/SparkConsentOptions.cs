using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Abstractions.Consent;

namespace MintPlayer.Spark.Consent;

/// <summary><c>Spark:Consent</c> (commerce PRD X21, GA9). Code given to <c>AddConsent</c> runs after the section.</summary>
public sealed class SparkConsentOptions : ISparkConfigurationSection
{
    public static string SectionName => "Spark:Consent";

    /// <summary>
    /// The version of the host's consent texts and policy. Bump it when a purpose's meaning, its vendors or the policy
    /// change: every decision taken under an older version reads as undecided and the banner asks again (consent must
    /// be "informed", GDPR Art. 4(11)). Purposes in <see cref="NonExpiringPurposes"/> are not re-asked.
    /// </summary>
    public string PolicyVersion { get; set; } = "1";

    /// <summary>The privacy or cookie policy page the banner links to (relative or absolute).</summary>
    public string? PolicyUrl { get; set; }

    /// <summary>
    /// How long a grant stays valid before the banner asks again. Default 13 months, the CNIL's recommendation for
    /// cookie consent (délibération n° 2020-091). ⚠️ A national authority's guidance, not EU law: confirm per country (D37).
    /// </summary>
    public TimeSpan ConsentValidity { get; set; } = TimeSpan.FromDays(395);

    /// <summary>
    /// How long a refusal is respected before the banner may ask again. Default 6 months, the CNIL's recommendation
    /// (it considers re-asking sooner a pressure on the visitor). ⚠️ Per-country point (D37).
    /// </summary>
    public TimeSpan RefusalValidity { get; set; } = TimeSpan.FromDays(182);

    /// <summary>
    /// Purposes whose decision neither expires nor goes stale with <see cref="PolicyVersion"/>: a marketing-mail opt-in
    /// stands until it is withdrawn (an unsubscribe link, the settings page). Default: <c>marketing-mail</c>.
    /// </summary>
    public List<string> NonExpiringPurposes { get; set; } = [SparkConsentPurposes.MarketingMail];

    /// <summary>
    /// The origin of unsubscribe links (<c>https://shop.example</c>). Null means <c>Spark:Auth:PublicBaseUrl</c>, else
    /// (Development only) the request's origin.
    /// </summary>
    public string? PublicOrigin { get; set; }

    /// <summary>The SPA route of the unsubscribe page (<c>@mintplayer/ng-spark/consent</c>'s <c>sparkConsentRoutes()</c>).</summary>
    public string UnsubscribePagePath { get; set; } = "/consent/unsubscribe";

    /// <summary>How many past decisions a record keeps as proof (the current decision per purpose is always kept).</summary>
    public int HistoryLimit { get; set; } = 100;
}
