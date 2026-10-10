namespace SeoTester;

/// <summary>
/// Which sites a gated feature applies to (<c>docs/seo_analyzer_PRD.md</c> D11): every gate is an administrator
/// setting with one of these values. "Verified" means the owning organization proved it controls the site (F3).
/// </summary>
public enum SeoFeatureScope
{
    /// <summary>The feature is off for every site.</summary>
    Off,

    /// <summary>Only sites whose organization verified ownership.</summary>
    VerifiedOnly,

    /// <summary>Every site, verified or not.</summary>
    AllSites,
}

public static class SeoFeatureScopeExtensions
{
    /// <summary>Whether the gate admits a site (D11, Q-M7a): the audit start, the monitoring sweep, its alerts and the site form ask the same question.</summary>
    public static bool Admits(this SeoFeatureScope gate, bool verified) => gate switch
    {
        SeoFeatureScope.AllSites => true,
        SeoFeatureScope.VerifiedOnly => verified,
        _ => false,
    };
}
