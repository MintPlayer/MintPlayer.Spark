using System.Globalization;
using SeoTester.Rendering;
using SeoTester.Scoring;

namespace SeoTester.Checks;

/// <summary>
/// The checks that need the rendered page (<c>docs/seo_analyzer_PRD.md</c> F12, §3.1 layer R, M10): judged from
/// <see cref="RenderedPageFacts"/> (one per sampled page) and, once per run, from the service worker and analytics
/// facts. Pure: the browser work is the host's (<c>Rendering/</c>). Thresholds where the research leaves a range are
/// stated on each check.
/// </summary>
public static class RenderChecks
{
    public const string PageWeight = "performance.page-weight";
    public const string LazyLoading = "performance.lazy-loading";
    public const string CodeSplitting = "performance.code-splitting";
    public const string Aria = "accessibility.aria";
    public const string ColorContrast = "accessibility.color-contrast";
    public const string TouchTargets = "accessibility.touch-targets";
    public const string HeadingOrder = "accessibility.heading-order";
    public const string Landmarks = "accessibility.landmarks";
    public const string AxeOther = "accessibility.axe-other";
    public const string Responsive = "mobile.responsive";
    public const string RawVsRendered = "technical.raw-vs-rendered";
    public const string MobileParity = "technical.mobile-parity";
    public const string ServiceWorker = "mobile.service-worker";
    public const string ServiceWorkerCaching = "mobile.service-worker-caching";
    public const string SpaAnalytics = "technical.spa-analytics";
    public const string AnalyticsConsent = "security.analytics-consent";

    /// <summary>Checks judged per rendered page.</summary>
    public static IReadOnlyList<string> PageIds { get; } =
        [PageWeight, LazyLoading, CodeSplitting, Aria, ColorContrast, TouchTargets, HeadingOrder, Landmarks, AxeOther, Responsive, RawVsRendered, MobileParity];

    /// <summary>Checks judged once per run.</summary>
    public static IReadOnlyList<string> SiteIds { get; } = [ServiceWorker, ServiceWorkerCaching, SpaAnalytics, AnalyticsConsent];

    public static IReadOnlyList<string> Ids { get; } = [.. PageIds, .. SiteIds];

    /// <summary>
    /// axe-core rules measured by a raw-HTML check already (alt text, form labels, language, title, viewport zoom, link
    /// text): not counted again under <see cref="AxeOther"/>.
    /// </summary>
    public static readonly IReadOnlySet<string> AxeRulesCoveredElsewhere = new HashSet<string>(StringComparer.Ordinal)
    {
        "image-alt", "input-image-alt", "label", "html-has-lang", "html-lang-valid", "html-xml-lang-mismatch", "document-title",
        "meta-viewport", "meta-viewport-large", "link-name",
    };

    /// <summary>The axe-core rule ids that make up each accessibility check (axe 4.13, tags wcag2a/aa, wcag21a/aa, wcag22aa, best-practice).</summary>
    public static string CheckForAxeRule(string rule) => rule switch
    {
        "color-contrast" => ColorContrast,
        "target-size" => TouchTargets,
        "heading-order" => HeadingOrder,
        "region" or "bypass" or "skip-link" or "landmark-one-main" => Landmarks,
        _ when rule.StartsWith("landmark-", StringComparison.Ordinal) => Landmarks,
        _ when rule.StartsWith("aria-", StringComparison.Ordinal) || rule is "presentation-role-conflict" => Aria,
        _ => AxeOther,
    };

    /// <summary>The page-level findings of one rendered page; a page that could not be rendered gives Error for each.</summary>
    public static IReadOnlyList<CheckFinding> Evaluate(RenderedPageFacts facts)
    {
        var url = facts.Url;
        if (facts.Error is { } error || facts.Mobile is null)
            return PageIds.Select(id => Finding.Error(id, facts.Error ?? "the page could not be rendered")).ToList();

        var findings = new List<CheckFinding>
        {
            Weight(facts, url),
            Lazy(facts.Mobile, url),
            Coverage(facts.Coverage, url),
            Layout(facts.Mobile, url),
        };
        findings.AddRange(Accessibility(facts.Axe, url));
        findings.Add(RawVersusRendered(facts.Raw, facts.Desktop ?? facts.Mobile, url));
        findings.Add(Parity(facts.Desktop, facts.Mobile, url));
        return findings;
    }

    /// <summary>
    /// Requests and bytes of the phone render. Bytes are decompressed (the router decompresses), so the limits sit above
    /// Lighthouse's transfer-size ones: warn above 80 requests or 3 MB, fail above 150 requests or 6 MB.
    /// </summary>
    private static CheckFinding Weight(RenderedPageFacts facts, string url)
    {
        var served = facts.Network.Where(n => n.Outcome == "fulfilled").ToList();
        var requests = served.Count;
        var kilobytes = served.Sum(n => n.Bytes) / 1024;
        var status = requests > 150 || kilobytes > 6 * 1024 ? CheckStatus.Fail
            : requests > 80 || kilobytes > 3 * 1024 ? CheckStatus.Warn
            : CheckStatus.Pass;
        return Finding.Make(PageWeight, status, [("url", url), ("n", requests), ("value", kilobytes)]);
    }

    /// <summary>The LCP image lazy-loaded fails; three or more eager images below the first screen warn.</summary>
    private static CheckFinding Lazy(RenderedView mobile, string url)
    {
        if (mobile.Images.Count == 0)
            return Finding.NotApplicable(LazyLoading);
        if (mobile.LcpTag is "IMG" && mobile.LcpLazy)
            return Finding.Fail(LazyLoading, ("url", url), ("n", 0), ("value", mobile.LcpSource));
        var eagerBelow = mobile.Images.Count(i => !i.AboveFold && !i.Lazy);
        return Finding.When(eagerBelow < 3, LazyLoading, CheckStatus.Warn, ("url", url), ("n", eagerBelow));
    }

    /// <summary>JavaScript of 150 KB or more of which half never ran warns; 1 MB with 60 % unused fails.</summary>
    private static CheckFinding Coverage(ScriptCoverage? coverage, string url)
    {
        if (coverage is null || coverage.Bytes == 0)
            return Finding.NotApplicable(CodeSplitting);
        var kilobytes = coverage.Bytes / 1024;
        var unused = (int)Math.Round(100.0 * coverage.UnusedBytes / coverage.Bytes);
        var status = kilobytes >= 1024 && unused >= 60 ? CheckStatus.Fail
            : kilobytes >= 150 && unused >= 50 ? CheckStatus.Warn
            : CheckStatus.Pass;
        return Finding.Make(CodeSplitting, status, [("url", url), ("value", kilobytes), ("n", unused)]);
    }

    /// <summary>Horizontal scrolling at 375 px fails; so does more than half the text below 12 px (Lighthouse: 60 % legible).</summary>
    private static CheckFinding Layout(RenderedView mobile, string url)
    {
        if (mobile.ScrollWidth > mobile.ViewportWidth + 1)
            return Finding.Fail(Responsive, ("url", url), ("value", mobile.ScrollWidth));
        if (mobile.TextCharacters >= 200 && mobile.SmallTextCharacters * 2 > mobile.TextCharacters)
            return Finding.Fail(Responsive, ("url", url), ("value", mobile.ScrollWidth),
                ("n", (int)Math.Round(100.0 * mobile.SmallTextCharacters / mobile.TextCharacters)));
        return Finding.Pass(Responsive, ("url", url));
    }

    /// <summary>One finding per accessibility check: a critical or serious violation fails, a moderate or minor one warns.</summary>
    private static IEnumerable<CheckFinding> Accessibility(IReadOnlyList<AxeViolation>? axe, string url)
    {
        string[] checks = [Aria, ColorContrast, TouchTargets, HeadingOrder, Landmarks, AxeOther];
        if (axe is null)
        {
            foreach (var check in checks)
                yield return Finding.Error(check, "the accessibility scan did not run");
            yield break;
        }
        var byCheck = axe.Where(v => !AxeRulesCoveredElsewhere.Contains(v.Id)).GroupBy(v => CheckForAxeRule(v.Id)).ToDictionary(g => g.Key, g => g.ToList());
        foreach (var check in checks)
        {
            if (!byCheck.TryGetValue(check, out var violations) || violations.Count == 0)
            {
                yield return Finding.Pass(check, ("url", url));
                continue;
            }
            var severe = violations.Any(v => v.Impact is "critical" or "serious");
            var nodes = violations.Sum(v => v.Nodes);
            var example = violations.OrderByDescending(v => Impact(v.Impact)).ThenByDescending(v => v.Nodes).First();
            yield return Finding.Make(check, severe ? CheckStatus.Fail : CheckStatus.Warn,
                [("url", url), ("n", nodes), ("value", example.Help ?? example.Id), ("rule", example.Id)]);
        }
    }

    /// <summary>
    /// A client-rendered page: the served HTML carries less than a fifth of the rendered words (of at least 100), or has
    /// no title or h1 that the rendered page has, fails; less than 60 % warns.
    /// </summary>
    private static CheckFinding RawVersusRendered(RawView? raw, RenderedView rendered, string url)
    {
        if (raw is null)
            return Finding.Error(RawVsRendered, "the served HTML was not seen");
        var detail = new (string, object?)[] { ("url", url), ("n", raw.Words), ("value", rendered.Words) };
        if (rendered.Words < 50 && rendered.H1Count == 0)
            return Finding.Make(RawVsRendered, CheckStatus.Pass, detail);
        var missingStructure = (string.IsNullOrWhiteSpace(raw.Title) && !string.IsNullOrWhiteSpace(rendered.Title)) || (raw.H1Count == 0 && rendered.H1Count > 0);
        if ((rendered.Words >= 100 && raw.Words * 5 < rendered.Words) || missingStructure)
            return Finding.Make(RawVsRendered, CheckStatus.Fail, detail);
        return Finding.Make(RawVsRendered, raw.Words * 10 < rendered.Words * 6 ? CheckStatus.Warn : CheckStatus.Pass, detail);
    }

    /// <summary>
    /// Mobile-first indexing: the phone render must carry the desktop render's content. Fewer than half the desktop words
    /// fails; fewer than 70 %, or more than a tenth of its same-site links missing (at least 3), warns.
    /// </summary>
    private static CheckFinding Parity(RenderedView? desktop, RenderedView mobile, string url)
    {
        if (desktop is null)
            return Finding.Error(MobileParity, "the desktop render failed");
        var mobileLinks = new HashSet<string>(mobile.Links, StringComparer.Ordinal);
        var missing = desktop.Links.Count(l => !mobileLinks.Contains(l));
        var detail = new (string, object?)[] { ("url", url), ("n", missing), ("value", mobile.Words), ("target", desktop.Words) };
        if (desktop.Words >= 50 && mobile.Words * 2 < desktop.Words)
            return Finding.Make(MobileParity, CheckStatus.Fail, detail);
        var warn = (desktop.Words >= 50 && mobile.Words * 10 < desktop.Words * 7) || (missing >= 3 && missing * 10 > desktop.Links.Count);
        return Finding.Make(MobileParity, warn ? CheckStatus.Warn : CheckStatus.Pass, detail);
    }

    // ---- Once per run. ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The run's site-level render findings. <paramref name="pwa"/>: the site's PWA profile (F2): without it a site with
    /// no service worker is not judged on one.
    /// </summary>
    public static IReadOnlyList<CheckFinding> EvaluateSite(ServiceWorkerFact? worker, AnalyticsFact? analytics, bool pwa)
    {
        var findings = new List<CheckFinding>();
        if (worker is null)
        {
            findings.Add(pwa ? Finding.Fail(ServiceWorker, ("value", "none")) : Finding.NotApplicable(ServiceWorker));
            findings.Add(Finding.NotApplicable(ServiceWorkerCaching));
        }
        else
        {
            findings.Add(worker.Status is >= 200 and < 300 && worker.HandlesFetch
                ? Finding.Pass(ServiceWorker, ("value", worker.ScriptUrl))
                : Finding.Warn(ServiceWorker, ("value", worker.ScriptUrl), ("status", worker.Status)));
            var maxAge = MaxAge(worker.CacheControl);
            findings.Add(worker.Status is >= 200 and < 300
                ? Finding.When(maxAge is null or <= 0, ServiceWorkerCaching, CheckStatus.Warn, ("value", maxAge ?? 0))
                : Finding.NotApplicable(ServiceWorkerCaching));
        }

        if (analytics is null || analytics.Tools.Count == 0)
        {
            findings.Add(Finding.NotApplicable(SpaAnalytics));
            findings.Add(Finding.NotApplicable(AnalyticsConsent));
            return findings;
        }
        var tools = string.Join(", ", analytics.Tools);
        // A hit before any interaction, or a tracker with no consent manager on the page, is a warning (Info, weight 0).
        findings.Add(analytics.HitsOnLoad > 0 || !analytics.ConsentManager
            ? Finding.Warn(AnalyticsConsent, ("value", tools), ("n", analytics.HitsOnLoad))
            : Finding.Pass(AnalyticsConsent, ("value", tools)));
        findings.Add(analytics.RouteChanged is true
            ? Finding.When(analytics.HitsAfterRouteChange > 0, SpaAnalytics, CheckStatus.Fail, ("value", tools), ("n", analytics.HitsAfterRouteChange))
            : Finding.NotApplicable(SpaAnalytics));
        return findings;
    }

    /// <summary>The <c>max-age</c> (or <c>s-maxage</c>) of a Cache-Control header; 0 for <c>no-cache</c>/<c>no-store</c>; null when absent.</summary>
    public static int? MaxAge(string? cacheControl)
    {
        if (string.IsNullOrWhiteSpace(cacheControl))
            return null;
        int? result = null;
        foreach (var part in cacheControl.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.Equals("no-cache", StringComparison.OrdinalIgnoreCase) || part.Equals("no-store", StringComparison.OrdinalIgnoreCase))
                return 0;
            var eq = part.IndexOf('=');
            if (eq > 0 && part[..eq].Trim().ToLowerInvariant() is "max-age" or "s-maxage"
                && int.TryParse(part[(eq + 1)..].Trim('"', ' '), NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
                result = Math.Max(result ?? 0, seconds);
        }
        return result;
    }

    private static int Impact(string? impact) => impact switch { "critical" => 4, "serious" => 3, "moderate" => 2, "minor" => 1, _ => 0 };
}
