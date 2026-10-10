using MintPlayer.Spark.Settings;

namespace SeoTester.Settings;

/// <summary>
/// Every feature gate and limit of <c>docs/seo_analyzer_PRD.md</c> §6 and the category weights of §7, as
/// administrator settings (D11, D15, X9). Each is read as the stored value, else <c>IConfiguration[Key]</c>, else the
/// default below, which is the PRD's. Only the <c>Administrators</c> group sees or changes them (<c>security.json</c>
/// binds <c>settings:administrators</c>). The per-user run limits are quotas: <see cref="SeoQuotas"/>.
/// </summary>
/// <remarks>Never a secret here: values are stored in plaintext. API keys stay in configuration (<c>SeoTester:PageSpeed:ApiKey</c>).</remarks>
public static class SeoSettings
{
    // ---- Feature gates (D11). Off, VerifiedOnly or AllSites. ---------------------------------------------------

    public static readonly SparkSetting<SeoFeatureScope> Monitoring = new("SeoTester:Features:Monitoring", SeoFeatureScope.VerifiedOnly,
        "Scheduled monitoring and its alerts (Off, VerifiedOnly, AllSites).");

    public static readonly SparkSetting<SeoFeatureScope> IntrusiveProbes = new("SeoTester:Features:IntrusiveProbes", SeoFeatureScope.VerifiedOnly,
        "Probes for exposed files (.git, .env, backups, directory listings) (Off, VerifiedOnly, AllSites).");

    public static readonly SparkSetting<SeoFeatureScope> DeepCrawl = new("SeoTester:Features:DeepCrawl", SeoFeatureScope.VerifiedOnly,
        "Which sites get the verified (deep-crawl) limits (Off, VerifiedOnly, AllSites).");

    public static readonly SparkSetting<SeoFeatureScope> Sharing = new("SeoTester:Features:Sharing", SeoFeatureScope.AllSites,
        "Share links and badges (Off, VerifiedOnly, AllSites). Off ends every existing link at once.");

    // ---- Limits per run (§6): unverified / verified. ------------------------------------------------------------

    public static readonly SparkSetting<int> UnverifiedPagesPerRun = new("SeoTester:Limits:Unverified:PagesPerRun", 100, "Pages crawled per run of an unverified site.");
    public static readonly SparkSetting<int> VerifiedPagesPerRun = new("SeoTester:Limits:Verified:PagesPerRun", 2000, "Pages crawled per run of a verified site.");

    public static readonly SparkSetting<TimeSpan> UnverifiedWallTime = new("SeoTester:Limits:Unverified:WallTime", TimeSpan.FromMinutes(10), "Longest run of an unverified site.");
    public static readonly SparkSetting<TimeSpan> VerifiedWallTime = new("SeoTester:Limits:Verified:WallTime", TimeSpan.FromMinutes(60), "Longest run of a verified site.");

    public static readonly SparkSetting<int> UnverifiedConnectionsPerHost = new("SeoTester:Limits:Unverified:ConnectionsPerHost", 1, "Parallel connections to an unverified host.");
    public static readonly SparkSetting<int> VerifiedConnectionsPerHost = new("SeoTester:Limits:Verified:ConnectionsPerHost", 4, "Parallel connections to a verified host.");

    public static readonly SparkSetting<TimeSpan> UnverifiedRequestInterval = new("SeoTester:Limits:Unverified:RequestInterval", TimeSpan.FromSeconds(1), "Least time between two requests to an unverified host.");

    public static readonly SparkSetting<int> UnverifiedRenderedSample = new("SeoTester:Limits:Unverified:RenderedSample", 3, "Pages rendered in the headless browser for an unverified site.");
    public static readonly SparkSetting<int> VerifiedRenderedSample = new("SeoTester:Limits:Verified:RenderedSample", 10, "Pages rendered in the headless browser for a verified site.");

    public static readonly SparkSetting<int> UnverifiedPsiSample = new("SeoTester:Limits:Unverified:PsiSample", 1, "Pages measured with PageSpeed Insights for an unverified site (the home page).");
    public static readonly SparkSetting<int> VerifiedPsiSample = new("SeoTester:Limits:Verified:PsiSample", 5, "Pages measured with PageSpeed Insights for a verified site.");

    // ---- Full-site crawl (D14): its weekly allowance is SeoQuotas.FullCrawls; this is the safety ceiling. --------

    public static readonly SparkSetting<int> FullCrawlMaxPages = new("SeoTester:Limits:FullCrawl:MaxPages", 20_000, "Safety ceiling on pages of a full-site crawl.");
    public static readonly SparkSetting<TimeSpan> FullCrawlWallTime = new("SeoTester:Limits:FullCrawl:WallTime", TimeSpan.FromHours(6), "Safety ceiling on the duration of a full-site crawl.");

    // ---- Across all users (D17, D37). -----------------------------------------------------------------------------

    public static readonly SparkSetting<TimeSpan> TargetCoolDown = new("SeoTester:Limits:Unverified:TargetCoolDown", TimeSpan.FromHours(6),
        "Least time between two runs of one unverified target, whoever starts them.");

    public static readonly SparkSetting<int> ConcurrentRenders = new("SeoTester:Limits:ConcurrentRenders", 1, "Headless-browser renders at a time, for the whole application.");

    public static readonly SparkSetting<TimeSpan> AuditLogRetention = new("SeoTester:AuditLog:Retention", TimeSpan.FromDays(365), "How long the abuse log keeps an entry.");

    // ---- Ownership verification (F3). ------------------------------------------------------------------------------

    public static readonly SparkSetting<TimeSpan> VerificationRecheckInterval = new("SeoTester:Verification:RecheckInterval", TimeSpan.FromDays(1),
        "How often a verified site's proof (DNS record, file or meta tag) is checked again.");

    public static readonly SparkSetting<int> VerificationFailuresBeforeWithdrawal = new("SeoTester:Verification:FailuresBeforeWithdrawal", 3,
        "Failed re-checks in a row after which a site's verification is withdrawn.");

    // ---- Category weights (§7, D15). A category with no measurements drops out of the weighted mean. -------------

    public static readonly SparkSetting<int> WeightSeo = new("SeoTester:Weights:Seo", 25, "Weight of the SEO category in the headline score.");
    public static readonly SparkSetting<int> WeightTechnical = new("SeoTester:Weights:Technical", 20, "Weight of the Technical category in the headline score.");
    public static readonly SparkSetting<int> WeightPerformance = new("SeoTester:Weights:Performance", 20, "Weight of the Performance category in the headline score.");
    public static readonly SparkSetting<int> WeightSecurity = new("SeoTester:Weights:Security", 15, "Weight of the Security category in the headline score.");
    public static readonly SparkSetting<int> WeightAccessibility = new("SeoTester:Weights:Accessibility", 10, "Weight of the Accessibility category in the headline score.");
    public static readonly SparkSetting<int> WeightSocial = new("SeoTester:Weights:Social", 5, "Weight of the Social category in the headline score.");
    public static readonly SparkSetting<int> WeightMobile = new("SeoTester:Weights:Mobile", 5, "Weight of the Mobile/PWA category in the headline score.");
}
