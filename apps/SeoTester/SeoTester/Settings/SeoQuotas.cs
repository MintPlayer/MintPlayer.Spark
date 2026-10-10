using MintPlayer.Spark.Settings;
using MintPlayer.Spark.Settings.Quotas;

namespace SeoTester.Settings;

/// <summary>
/// The per-user run limits of <c>docs/seo_analyzer_PRD.md</c> §6 as quotas (X3, D10, D14): sliding windows, their
/// limits and windows administrator settings like <see cref="SeoSettings"/>. <c>StartAudit</c> (M9) consumes them in
/// code, because joining a running audit does not count and verified sites have their own allowance.
/// </summary>
public static class SeoQuotas
{
    /// <summary>Runs a user starts on unverified sites.</summary>
    public static readonly SparkQuota Audits = new("audits",
        new SparkSetting<int>("SeoTester:Quotas:Audits:Limit", 5, "Runs a user may start on unverified sites per window."),
        new SparkSetting<TimeSpan>("SeoTester:Quotas:Audits:Window", TimeSpan.FromDays(1), "The window of the unverified runs quota."));

    /// <summary>Runs a user starts on verified sites, scheduled runs included.</summary>
    public static readonly SparkQuota VerifiedAudits = new("verified-audits",
        new SparkSetting<int>("SeoTester:Quotas:VerifiedAudits:Limit", 24, "Runs a user may start on verified sites per window, scheduled runs included."),
        new SparkSetting<TimeSpan>("SeoTester:Quotas:VerifiedAudits:Window", TimeSpan.FromDays(1), "The window of the verified runs quota."));

    /// <summary>Full-site crawls per user, across all their verified sites (D14).</summary>
    public static readonly SparkQuota FullCrawls = new("full-crawls",
        new SparkSetting<int>("SeoTester:Quotas:FullCrawls:Limit", 1, "Full-site crawls a user may start per window, across all their verified sites."),
        new SparkSetting<TimeSpan>("SeoTester:Quotas:FullCrawls:Window", TimeSpan.FromDays(7), "The window of the full-site crawl quota."));
}
