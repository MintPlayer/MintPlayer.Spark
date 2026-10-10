using System.IO.Compression;
using System.Text;
using SeoTester.Checks;
using SeoTester.Crawling;
using SeoTester.Dns;
using SeoTester.Entities;
using SeoTester.Fetching;
using SeoTester.Html;

namespace SeoTester.Crawler;

/// <summary>What the probe learned: robots.txt, the facts for the site-level checks, the home page and the sitemap's URLs.</summary>
/// <param name="Home">The home page, crawled as the run's first page; null when robots.txt blocked the crawl.</param>
public sealed record ProbeOutcome(RobotsTxt Robots, RobotsFacts RobotsFacts, SiteFacts Facts, AnalyzedPage? Home, IReadOnlyList<string> SitemapUrls)
{
    /// <summary>robots.txt does not let the crawler fetch the home page: nothing else is requested from the host (D19).</summary>
    public bool Blocked => Home is null;
}

/// <summary>
/// The site-level probe of <c>seo-probe</c> (<c>docs/seo_analyzer_PRD.md</c> §4.3): robots.txt first (Q-M8e: the
/// opt-out is honoured before any page is fetched), then the home page, sitemaps, the http→https and www/apex
/// redirects, TLS, the well-known files, the home page's manifest, icons and assets, DNS, RDAP and HSTS preload.
/// Requests to the site are paced like the crawl (<see cref="HostPacer"/>) and asked only for paths robots.txt allows;
/// probes for exposed files run only when the run was granted them (verified sites by default, D11). A failing
/// measurement makes its checks "error" (never a fail) and the probe carries on.
/// </summary>
public sealed class SiteProber(ISeoFetcher fetcher, ExternalLookups lookups, TimeProvider time, ILogger<SiteProber> logger)
{
    private const int SmallFile = 64 * 1024;
    private const int MaxSitemapBytes = 10 * 1024 * 1024;
    private const int MaxChildSitemaps = 3;
    private const int MaxAssetsOfEachKind = 3;

    public async Task<ProbeOutcome> ProbeAsync(AuditRun run, HostPacer pacer, CancellationToken cancellationToken)
    {
        var home = new Uri(run.Origin + "/");
        var host = home.IdnHost;
        var facts = new SiteFacts { Home = home, NowUtc = time.GetUtcNow().UtcDateTime, Intrusive = run.IntrusiveProbes };

        // 1. robots.txt (RFC 9309 §2.3.1): 2xx rules, 4xx allow all, 5xx or no answer disallow all.
        var robotsFetch = await FetchAsync(pacer, new Uri(home, "/robots.txt"), RobotsTxt.MaxBytes, cancellationToken);
        RobotsTxt robots = robotsFetch.Ok ? RobotsTxt.Parse(robotsFetch.Text(), SeoTesterBot.RobotsToken)
            : robotsFetch.StatusCode is >= 400 and < 500 ? RobotsTxt.AllowAll
            : RobotsTxt.DisallowAll;
        var robotsFacts = new RobotsFacts(robots.Availability, robotsFetch.StatusCode == 0 ? null : robotsFetch.StatusCode, robots, robotsFetch.Body?.LongLength ?? 0);
        facts.Robots = robotsFacts;
        if (robots.DisallowsAll)
            return new ProbeOutcome(robots, robotsFacts, facts, null, []);

        // 2. The home page: the run's first page.
        var homeFetch = await FetchAsync(pacer, home, PageFetcher.MaxPageBytes, cancellationToken);
        var homePage = PageAnalyzer.Analyze(homeFetch, new FrontierEntry(home.AbsoluteUri, 0, null, false), run, host, linkedInternally: false, facts.NowUtc);
        // A home page that redirects (/ → /en/) is judged under its target's URL by the crawl; its resources (manifest,
        // icons, assets, login form) are still read from where it led, when that is on the site.
        var homeHtml = homePage.Input.Html
            ?? (homeFetch.Ok && homeFetch.IsHtml && homeFetch.FinalUrl is { } final && CrawlUrl.IsSameHost(final, host) && homeFetch.Text() is { } text
                ? HtmlPage.Parse(text) : null);
        facts.HomeHttpVersion = homeFetch.HttpVersion;
        facts.HomeAltSvc = homeFetch.Header("alt-svc");
        facts.HomeHasLoginForm = homeHtml?.Document.QuerySelector("input[type=password]") is not null;

        // 3. Sitemaps.
        var (sitemap, sitemapUrls) = await SitemapsAsync(pacer, robots, home, host, Math.Max(1, run.MaxPages), cancellationToken);
        facts.Sitemap = sitemap;

        // 4. Redirects: http → https, and the other of www / apex.
        if (facts.IsHttps && home.IsDefaultPort)
            facts.HttpToHttps = Redirect(await FetchAsync(pacer, new Uri($"http://{host}/"), SmallFile, cancellationToken));
        facts.WwwVariant = await WwwVariantAsync(pacer, home, host, cancellationToken);

        // 5. TLS.
        if (facts.IsHttps)
            facts.Tls = await Guard(() => fetcher.ProbeTlsAsync(host, home.Port, cancellationToken), "TLS");

        // 6. Well-known files, the favicon and a page that cannot exist (soft 404).
        facts.SecurityTxt = await FileAsync(pacer, robots, home, "/.well-known/security.txt", SmallFile, cancellationToken);
        if (facts.HomeHasLoginForm)
            facts.ChangePassword = await FileAsync(pacer, robots, home, "/.well-known/change-password", SmallFile, cancellationToken);
        facts.Favicon = await FileAsync(pacer, robots, home, "/favicon.ico", SmallFile, cancellationToken, keepBody: false);
        facts.NotFoundProbe = await FileAsync(pacer, robots, home, $"/seotester-{Guid.NewGuid():N}-does-not-exist", SmallFile, cancellationToken, keepBody: false);

        // 7. Probes for exposed files and folder listings: only with the IntrusiveProbes gate (D11; Belgian CC art. 550bis).
        if (run.IntrusiveProbes)
        {
            foreach (var path in SiteChecks.ExposedFilePaths)
                if (await FileAsync(pacer, robots, home, path, 4096, cancellationToken) is { } file)
                    facts.ExposedFiles.Add(file);
            if (homeHtml is not null)
                foreach (var directory in SiteParsers.AssetDirectories(homeHtml, home, 3))
                    if (await FileAsync(pacer, robots, home, directory, SmallFile, cancellationToken) is { } listing)
                        facts.Directories.Add(listing);
        }

        // 8. The home page's manifest, icons, OpenSearch description, stylesheets and scripts.
        if (homeHtml is not null)
            await HomeResourcesAsync(pacer, robots, home, homeHtml, facts, cancellationToken);

        // 9. DNS, RDAP and the HSTS preload list (third parties and resolvers, not the site).
        var apex = await Guard(() => fetcher.ZoneApexAsync(host, cancellationToken), "SOA") ?? Fallback(host);
        facts.Dns = await Guard(() => DnsAsync(host, apex, cancellationToken), "DNS");
        facts.Rdap = await Guard(() => lookups.RdapAsync(apex, cancellationToken), "RDAP");
        if (facts.IsHttps)
        {
            facts.HstsPreloadStatus = await Guard(() => lookups.HstsPreloadAsync(apex, cancellationToken), "hstspreload");
            facts.HstsPreloadFailed = facts.HstsPreloadStatus is null;
        }

        return new ProbeOutcome(robots, robotsFacts, facts, homePage, sitemapUrls);
    }

    private async Task<FetchResult> FetchAsync(HostPacer pacer, Uri url, int maxBytes, CancellationToken cancellationToken, HttpMethod? method = null)
    {
        using (await pacer.WaitAsync(cancellationToken))
        {
            var result = await fetcher.FetchAsync(url, maxBytes, cancellationToken, method);
            if (result.RetryAfter is { } wait)
                pacer.Defer(wait > TimeSpan.FromMinutes(2) ? TimeSpan.FromMinutes(2) : wait);
            return result;
        }
    }

    /// <summary>A path of the site, fetched only when robots.txt allows it; null otherwise (its check is then N/A).</summary>
    private async Task<FetchFacts?> FileAsync(HostPacer pacer, RobotsTxt robots, Uri home, string path, int maxBytes, CancellationToken cancellationToken, bool keepBody = true)
    {
        if (!robots.IsAllowed(path))
            return null;
        var url = new Uri(home, path);
        var result = await FetchAsync(pacer, url, maxBytes, cancellationToken);
        var body = keepBody && result.Ok ? result.Text() : null;
        return new FetchFacts(url.AbsoluteUri, result.StatusCode == 0 ? null : result.StatusCode, result.ContentType,
            body is { Length: > 8192 } ? body[..8192] : body, result.Hops.Count > 0 ? result.FinalUrl?.AbsoluteUri : null, result.Error);
    }

    private static RedirectFacts Redirect(FetchResult result)
        => new(result.Requested.AbsoluteUri, result.Hops, result.FinalUrl?.AbsoluteUri, result.StatusCode == 0 ? null : result.StatusCode, result.Error);

    private async Task<RedirectFacts?> WwwVariantAsync(HostPacer pacer, Uri home, string host, CancellationToken cancellationToken)
    {
        var other = host.StartsWith("www.", StringComparison.Ordinal) ? host[4..] : "www." + host;
        if (!other.Contains('.'))
            return null;
        var url = new Uri($"{home.Scheme}://{other}/");
        var a = await fetcher.QueryDnsAsync(other, SeoDnsType.A, cancellationToken);
        var aaaa = await fetcher.QueryDnsAsync(other, SeoDnsType.Aaaa, cancellationToken);
        if (a is null && aaaa is null)
            return null; // could not ask: not measured
        if ((a?.Count ?? 0) + (aaaa?.Count ?? 0) == 0)
            return new RedirectFacts(url.AbsoluteUri, [], null, null, "does not resolve");
        return Redirect(await FetchAsync(pacer, url, SmallFile, cancellationToken));
    }

    private async Task<(SitemapFacts Facts, IReadOnlyList<string> Urls)> SitemapsAsync(HostPacer pacer, RobotsTxt robots, Uri home, string host, int keep, CancellationToken cancellationToken)
    {
        var fromRobots = robots.Sitemaps
            .Select(s => Uri.TryCreate(s, UriKind.Absolute, out var u) ? u : null)
            .Where(u => u is not null && CrawlUrl.IsSameHost(u, host) && robots.IsAllowed(u))
            .Select(u => u!)
            .ToList();
        var candidates = fromRobots.Count > 0 ? fromRobots : [new Uri(home, "/sitemap.xml")];
        var urls = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        SitemapFacts? first = null;
        foreach (var candidate in candidates.Take(MaxChildSitemaps))
        {
            if (!robots.IsAllowed(candidate))
                continue;
            var (content, status, error) = await ReadSitemapAsync(pacer, candidate, keep, cancellationToken);
            if (content is null)
            {
                first ??= new SitemapFacts(fromRobots.Count > 0 ? candidate.AbsoluteUri : null, status, fromRobots.Count > 0 ? error : null, 0, false, 0, fromRobots.Count > 0);
                continue;
            }
            var children = 0;
            if (content.IsIndex)
            {
                foreach (var child in content.Locations.Take(MaxChildSitemaps))
                {
                    if (!Uri.TryCreate(child, UriKind.Absolute, out var childUrl) || !CrawlUrl.IsSameHost(childUrl, host) || !robots.IsAllowed(childUrl))
                        continue;
                    children++;
                    var (childContent, _, _) = await ReadSitemapAsync(pacer, childUrl, keep, cancellationToken);
                    if (childContent is { IsIndex: false })
                        Collect(childContent.Locations);
                }
            }
            else
                Collect(content.Locations);
            if (first is null || first.Url is null)
                first = new SitemapFacts(candidate.AbsoluteUri, status, content.Error, content.Total, content.HasStylesheet, children, fromRobots.Count > 0);
            if (urls.Count >= keep)
                break;
        }
        return (first ?? new SitemapFacts(null, null, null, 0, false, 0, false), urls);

        void Collect(IEnumerable<string> locations)
        {
            foreach (var location in locations)
            {
                if (urls.Count >= keep)
                    return;
                if (Uri.TryCreate(location, UriKind.Absolute, out var u) && CrawlUrl.IsSameHost(u, host) && CrawlUrl.Normalize(u).AbsoluteUri is var key && seen.Add(key))
                    urls.Add(key);
            }
        }
    }

    private async Task<(SitemapContent? Content, int? Status, string? Error)> ReadSitemapAsync(HostPacer pacer, Uri url, int keep, CancellationToken cancellationToken)
    {
        var result = await FetchAsync(pacer, url, MaxSitemapBytes, cancellationToken);
        if (!result.Ok || result.Body is null)
            return (null, result.StatusCode == 0 ? null : result.StatusCode, result.Error ?? $"returns {result.StatusCode}");
        try
        {
            Stream stream = new MemoryStream(result.Body);
            // A .gz sitemap is a gzip file (not Content-Encoding): unzip it, capped like any sitemap.
            if (result.Body.Length > 2 && result.Body[0] == 0x1F && result.Body[1] == 0x8B)
                stream = new GZipStream(stream, CompressionMode.Decompress);
            await using (stream)
            {
                if (result.ContentType is { } type && type.Contains("text/plain", StringComparison.OrdinalIgnoreCase))
                {
                    using var reader = new StreamReader(stream, Encoding.UTF8);
                    var buffer = new char[(int)Math.Min(SitemapReader.MaxCharacters, 16 * 1024 * 1024)];
                    var read = await reader.ReadBlockAsync(buffer, cancellationToken);
                    return (SitemapReader.ReadText(new string(buffer, 0, read), keep), result.StatusCode, null);
                }
                return (SitemapReader.Read(stream, keep), result.StatusCode, null);
            }
        }
        catch (InvalidDataException)
        {
            return (new SitemapContent(false, [], 0, false, "not a valid gzip file"), result.StatusCode, null);
        }
    }

    private async Task HomeResourcesAsync(HostPacer pacer, RobotsTxt robots, Uri home, HtmlPage html, SiteFacts facts, CancellationToken cancellationToken)
    {
        // Manifest.
        if (html.Links("manifest").FirstOrDefault()?.GetAttribute("href") is { } manifestHref)
        {
            var manifestUrl = CrawlUrl.Resolve(home, manifestHref);
            if (manifestUrl is null || !CrawlUrl.IsSameHost(manifestUrl, home.IdnHost) || !robots.IsAllowed(manifestUrl))
                facts.Manifest = new ManifestFacts(manifestHref, null, manifestUrl is null ? ["invalid link"] : []);
            else
            {
                var manifest = await FetchAsync(pacer, manifestUrl, 256 * 1024, cancellationToken);
                facts.Manifest = new ManifestFacts(manifestUrl.AbsoluteUri, manifest.StatusCode,
                    manifest.Ok ? SiteParsers.ManifestProblems(manifest.Text()) : [$"returns {(manifest.StatusCode == 0 ? manifest.Error : manifest.StatusCode)}"]);
            }
        }

        facts.HasIconLink = html.Links("icon").Any();
        facts.HasAppleTouchIcon = html.Links("apple-touch-icon").Any();

        if (html.Links("search").FirstOrDefault(l => string.Equals(l.GetAttribute("type"), "application/opensearchdescription+xml", StringComparison.OrdinalIgnoreCase))
                ?.GetAttribute("href") is { } openSearchHref)
        {
            var openSearchUrl = CrawlUrl.Resolve(home, openSearchHref);
            facts.OpenSearch = openSearchUrl is not null && CrawlUrl.IsSameHost(openSearchUrl, home.IdnHost) && robots.IsAllowed(openSearchUrl)
                ? await FileAsync(pacer, robots, home, openSearchUrl.PathAndQuery, SmallFile, cancellationToken)
                : new FetchFacts(openSearchHref, null, null, null, null, "not on this site");
        }

        // Same-site stylesheets and scripts: compression, caching, minification, and the CSS features.
        var (styles, scripts) = SiteParsers.Assets(html, home, MaxAssetsOfEachKind);
        var cssTexts = html.Document.QuerySelectorAll("style").Select(s => s.TextContent).ToList();
        foreach (var (url, kind) in styles.Select(u => (u, "css")).Concat(scripts.Select(u => (u, "js"))))
        {
            if (!robots.IsAllowed(url))
                continue;
            var asset = await FetchAsync(pacer, url, 2 * 1024 * 1024, cancellationToken);
            var text = asset.Ok ? asset.Text() : null;
            var (minified, saving) = text is null ? (null, 0L) : SiteParsers.JudgeMinified(text);
            facts.Assets.Add(new AssetFacts(url.AbsoluteUri, kind, asset.StatusCode == 0 ? null : asset.StatusCode, asset.WireEncoding,
                asset.Header("cache-control"), asset.Body?.LongLength ?? 0, minified, saving));
            if (kind == "css" && text is not null)
                cssTexts.Add(text);
        }
        var googleFonts = html.Links("stylesheet").Select(l => l.GetAttribute("href") ?? string.Empty)
            .Where(h => h.Contains("fonts.googleapis.com", StringComparison.OrdinalIgnoreCase)).ToList();
        var colorScheme = html.Meta("color-scheme") is { } scheme && scheme.Contains("dark", StringComparison.OrdinalIgnoreCase);
        facts.Css = SiteParsers.ScanCss(cssTexts, googleFonts, colorScheme);
    }

    private async Task<DnsFacts> DnsAsync(string host, string apex, CancellationToken cancellationToken)
    {
        // CAA: the closest name from the host up to the zone apex that has any (RFC 8659 §3).
        IReadOnlyList<string>? caa = [];
        for (var name = host; ; name = name[(name.IndexOf('.') + 1)..])
        {
            caa = await fetcher.QueryDnsAsync(name, SeoDnsType.Caa, cancellationToken);
            if (caa is null || caa.Count > 0 || name == apex || !name.Contains('.'))
                break;
        }
        var ds = await fetcher.QueryDnsAsync(apex, SeoDnsType.Ds, cancellationToken);
        var nameservers = await fetcher.QueryDnsAsync(apex, SeoDnsType.Ns, cancellationToken);
        var addresses = new List<string>();
        foreach (var ns in (nameservers ?? []).Take(4))
            addresses.AddRange(await fetcher.QueryDnsAsync(ns, SeoDnsType.A, cancellationToken) ?? []);
        var mx = await fetcher.QueryDnsAsync(apex, SeoDnsType.Mx, cancellationToken);
        return new DnsFacts(
            apex,
            caa,
            ds is null ? null : ds.Count > 0,
            await fetcher.QueryDnsAsync(apex, SeoDnsType.Txt, cancellationToken),
            await fetcher.QueryDnsAsync("_dmarc." + apex, SeoDnsType.Txt, cancellationToken),
            await fetcher.QueryDnsAsync("_mta-sts." + apex, SeoDnsType.Txt, cancellationToken),
            mx is null ? null : mx.Count > 0,
            await fetcher.QueryDnsAsync(host, SeoDnsType.Aaaa, cancellationToken),
            nameservers,
            addresses);
    }

    /// <summary>Without an SOA answer: the last two labels (wrong for co.uk-style suffixes, which then show "not published").</summary>
    private static string Fallback(string host)
    {
        var labels = host.Split('.');
        return labels.Length <= 2 ? host : string.Join('.', labels[^2..]);
    }

    private async Task<T?> Guard<T>(Func<Task<T>> measure, string what)
    {
        try
        {
            return await measure();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "The {What} probe failed", what);
            return default;
        }
    }
}
