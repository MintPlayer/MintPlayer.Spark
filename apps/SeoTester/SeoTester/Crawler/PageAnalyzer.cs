using System.Globalization;
using SeoTester.Checks;
using SeoTester.Crawling;
using SeoTester.Entities;
using SeoTester.Html;

namespace SeoTester.Crawler;

/// <summary>A fetched page, judged: its stored result and the links that extend the frontier.</summary>
public sealed record AnalyzedPage(PageResult Result, PageInput Input, IReadOnlyList<Uri> InternalLinks, IReadOnlyList<Uri> ExternalLinks);

/// <summary>
/// Turns one <see cref="FetchResult"/> into a <see cref="PageResult"/> (id <c>{runId}/pages/{hash}</c>, so a retried batch
/// overwrites rather than duplicates, §4.3) with the page-level findings of <see cref="PageChecks"/>, and the page's links.
/// </summary>
public static class PageAnalyzer
{
    /// <summary>How long a page's details are kept (D8): also set by the model's <c>expiresAfter</c>, but a raw session does not apply it.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(7);

    public static AnalyzedPage Analyze(FetchResult fetch, FrontierEntry entry, AuditRun run, string host, bool linkedInternally, DateTime nowUtc)
    {
        HtmlPage? html = null;
        // A URL that redirected is judged as a redirect (chain, status), not by the content it led to: that content is
        // the target's, which is queued and judged under its own URL. (Judging it here counted /old and /a as two pages
        // with one title: red in CrawlPipelineTests before this.)
        var redirected = fetch.Hops.Count > 0;
        if (!redirected && fetch.Ok && fetch.IsHtml && fetch.Text() is { } text)
            html = HtmlPage.Parse(text);
        var input = new PageInput
        {
            Url = fetch.FinalUrl ?? fetch.Requested,
            StatusCode = fetch.StatusCode,
            Headers = fetch.Headers,
            SetCookies = fetch.SetCookies,
            WireEncoding = fetch.WireEncoding,
            BodyBytes = fetch.Body?.LongLength ?? 0,
            Prefix = fetch.Prefix,
            Html = html,
            InSitemap = entry.FromSitemap,
            LinkedInternally = linkedInternally,
        };
        var findings = PageChecks.Evaluate(input);
        var (internalLinks, externalLinks) = html is not null && !input.NoFollow
            ? SiteParsers.Links(html, input.Url, host)
            : ([], []);
        if (redirected && fetch.FinalUrl is { } target && CrawlUrl.IsSameHost(target, host))
            internalLinks = [CrawlUrl.Normalize(target)];

        var result = new PageResult
        {
            Id = PageResult.IdFor(run.Id!, entry.Url),
            RunId = run.Id,
            OrganizationId = run.OrganizationId,
            Url = entry.Url,
            StatusCode = fetch.StatusCode,
            Depth = entry.Depth,
            LoadMs = fetch.StatusCode == 0 ? null : fetch.ElapsedMs,
            Title = Cut(html?.Title, 300),
            Description = Cut(html?.Description, 500),
            H1 = Cut(html?.H1s.FirstOrDefault(), 300),
            OgTitle = Cut(html?.MetaProperty("og:title")?.Trim(), 300),
            OgDescription = Cut(html?.MetaProperty("og:description")?.Trim(), 500),
            OgImage = Cut(html?.MetaProperty("og:image")?.Trim(), 2000),
            OgUrl = Cut(html?.MetaProperty("og:url")?.Trim(), 2000),
            Findings = findings.Where(f => f.Status != Scoring.CheckStatus.NotApplicable).Select(PageFinding.From).ToList(),
            ExpiresAtUtc = nowUtc + Retention,
            FinalUrl = fetch.Hops.Count > 0 ? fetch.FinalUrl?.AbsoluteUri : null,
            RedirectHops = fetch.Hops.Count,
            FirstHopStatus = fetch.Hops.Count > 0 ? fetch.Hops[0].Status : null,
            Canonical = PageChecks.CanonicalOf(input)?.AbsoluteUri,
            Alternates = PageChecks.AlternatesOf(input).Select(a => a.Url.AbsoluteUri).Distinct().ToList(),
            Referrer = entry.Referrer,
            InSitemap = entry.FromSitemap,
            LinkedInternally = linkedInternally,
            NoIndex = input.NoIndex,
            Fingerprint = html is not null ? CrawlChecks.Fingerprint(html.BodyText).ToString("x16", CultureInfo.InvariantCulture) : null,
        };
        return new AnalyzedPage(result, input, internalLinks, externalLinks);
    }

    /// <summary>A stored page as the crawl-level checks read it.</summary>
    public static CrawledPage ToCrawled(PageResult page) => new(
        page.Url, page.StatusCode, page.Depth, page.Title, page.Description, page.RedirectHops, page.FirstHopStatus, page.FinalUrl,
        page.Canonical, page.Alternates, page.Referrer, page.InSitemap, page.LinkedInternally, page.NoIndex,
        page.Fingerprint is { } hex && ulong.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var print) ? print : null);

    private static string? Cut(string? value, int max) => value is { } v && v.Length > max ? v[..max] : value;
}
