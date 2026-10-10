namespace SeoTester.Rendering;

/// <summary>
/// Analytics and advertising tools recognised by their requests in the rendered page (<c>docs/seo_analyzer_PRD.md</c>
/// research §3.6: GA4, GTM; plus the common others), and consent managers. A <b>hit</b> (a request that records a
/// page view) is never sent on: the router answers it with 204 itself, so an audit never pollutes the site's
/// statistics, and counts it.
/// </summary>
public static class AnalyticsVendors
{
    private static readonly (string Tool, string Host, string? Path)[] Scripts =
    [
        ("Google Analytics", "www.googletagmanager.com", "/gtag/js"),
        ("Google Tag Manager", "www.googletagmanager.com", "/gtm.js"),
        ("Google Analytics", "www.google-analytics.com", "/analytics.js"),
        ("Meta Pixel", "connect.facebook.net", "/fbevents.js"),
        ("Microsoft Clarity", "www.clarity.ms", "/tag/"),
        ("Hotjar", "static.hotjar.com", null),
        ("Plausible", "plausible.io", "/js/"),
        ("LinkedIn Insight", "snap.licdn.com", null),
        ("Microsoft Advertising", "bat.bing.com", "/bat.js"),
        ("TikTok Pixel", "analytics.tiktok.com", null),
    ];

    private static readonly (string Tool, string HostSuffix, string PathPrefix)[] Hits =
    [
        ("Google Analytics", "google-analytics.com", "/g/collect"),
        ("Google Analytics", "google-analytics.com", "/collect"),
        ("Google Analytics", "google-analytics.com", "/j/collect"),
        ("Google Analytics", "analytics.google.com", "/g/collect"),
        ("Meta Pixel", "facebook.com", "/tr"),
        ("Microsoft Clarity", "clarity.ms", "/collect"),
        ("Plausible", "plausible.io", "/api/event"),
        ("LinkedIn Insight", "px.ads.linkedin.com", "/"),
        ("Microsoft Advertising", "bat.bing.com", "/action"),
        ("TikTok Pixel", "analytics.tiktok.com", "/api/"),
    ];

    private static readonly string[] ConsentManagerHosts =
    [
        "consent.cookiebot.com", "cdn.cookielaw.org", "sdk.privacy-center.org", "cdn-cookieyes.com", "app.usercentrics.eu",
        "web.cmp.usercentrics.eu", "cmp.quantcast.com", "cdn.iubenda.com", "consent.trustarc.com", "policy.app.cookieinformation.com",
        "cdn.cookie-script.com", "cmp.osano.com", "cdn.consentmanager.net",
    ];

    /// <summary>The tool a script request loads, or null.</summary>
    public static string? ToolForScript(Uri url)
    {
        foreach (var (tool, host, path) in Scripts)
            if (url.Host.Equals(host, StringComparison.OrdinalIgnoreCase) && (path is null || url.AbsolutePath.StartsWith(path, StringComparison.OrdinalIgnoreCase)))
                return tool;
        // Self-hosted Matomo.
        return url.AbsolutePath.EndsWith("/matomo.js", StringComparison.OrdinalIgnoreCase) || url.AbsolutePath.EndsWith("/piwik.js", StringComparison.OrdinalIgnoreCase)
            ? "Matomo" : null;
    }

    /// <summary>The tool a request records a hit for, or null.</summary>
    public static string? ToolForHit(Uri url)
    {
        foreach (var (tool, suffix, prefix) in Hits)
            if (HostIs(url.Host, suffix) && url.AbsolutePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return tool;
        return url.AbsolutePath.EndsWith("/matomo.php", StringComparison.OrdinalIgnoreCase) || url.AbsolutePath.EndsWith("/piwik.php", StringComparison.OrdinalIgnoreCase)
            ? "Matomo" : null;
    }

    public static bool IsConsentManager(Uri url) => ConsentManagerHosts.Any(h => url.Host.Equals(h, StringComparison.OrdinalIgnoreCase));

    private static bool HostIs(string host, string suffix)
        => host.Equals(suffix, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + suffix, StringComparison.OrdinalIgnoreCase);
}
