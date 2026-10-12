using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using SeoTester.Scoring;

namespace SeoTester.Checks;

/// <summary>What the aggregate knows about one crawled URL (a <c>PageResult</c>'s stored fields).</summary>
/// <param name="Depth">Clicks from the home page; -1 when the URL was only known from a sitemap.</param>
/// <param name="FirstHopStatus">The status of the first redirect, when there was one.</param>
/// <param name="Fingerprint">A 64-bit SimHash of the visible text (<see cref="CrawlChecks.Fingerprint"/>); null for non-HTML.</param>
public sealed record CrawledPage(
    string Url, int StatusCode, int Depth, string? Title, string? Description, int RedirectHops, int? FirstHopStatus, string? FinalUrl,
    string? Canonical, IReadOnlyList<string> Alternates, string? Referrer, bool InSitemap, bool LinkedInternally, bool NoIndex, ulong? Fingerprint);

/// <summary>An external link the crawl checked (HEAD, verified sites only).</summary>
public sealed record ExternalLinkResult(string Url, string Referrer, int? Status, string? Error);

/// <summary>A finding the aggregate made, about one URL (or the site when <paramref name="Url"/> is null).</summary>
public sealed record CrawlFinding(string? Url, CheckFinding Finding);

/// <summary>
/// The site-level checks of the crawl (catalogue layer S, <c>docs/seo_analyzer_PRD.md</c> §4.3 <c>seo-aggregate</c>):
/// duplicates, link graph, redirects, sitemap coverage, depth, near-duplicate content and hreflang return links.
/// Each yields one finding per URL concerned, so they score as a pass ratio like page checks.
/// </summary>
public static class CrawlChecks
{
    public static IReadOnlyList<string> Ids { get; } =
    [
        "seo.duplicate-titles", "seo.duplicate-descriptions", "seo.canonical", "seo.hreflang",
        "technical.sitemap", "technical.broken-internal-links", "technical.broken-external-links", "technical.redirect-chains",
        "technical.orphan-pages", "technical.crawl-depth", "technical.duplicate-content",
    ];

    /// <summary>Above this many pages near-duplicates are matched by exact fingerprint only (the pairwise scan is quadratic).</summary>
    public const int NearDuplicateLimit = 5000;

    /// <param name="sitemapUrls">The sitemap's URLs the crawl kept (at most the page limit).</param>
    /// <param name="externalLinks">Null when external links were not checked (unverified sites).</param>
    /// <param name="isLinked">Whether any crawled page links to a URL (also one never fetched); defaults to the pages' own flags.</param>
    public static IReadOnlyList<CrawlFinding> Evaluate(IReadOnlyList<CrawledPage> pages, IReadOnlyCollection<string> sitemapUrls, IReadOnlyList<ExternalLinkResult>? externalLinks,
        Func<string, bool>? isLinked = null)
    {
        var findings = new List<CrawlFinding>();
        var html = pages.Where(p => p.StatusCode == 200 && p.Fingerprint is not null).ToList();
        var byUrl = pages.GroupBy(p => p.Url, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        Duplicates(findings, html, p => p.Title, "seo.duplicate-titles");
        Duplicates(findings, html, p => p.Description, "seo.duplicate-descriptions");

        // Canonical targets that were crawled must answer 200 without a redirect and be indexable.
        foreach (var page in html.Where(p => p.Canonical is not null && p.Canonical != p.Url))
            if (byUrl.TryGetValue(page.Canonical!, out var target) && (target.StatusCode != 200 || target.RedirectHops > 0 || target.NoIndex))
                findings.Add(new(page.Url, Finding.Fail("seo.canonical", ("url", page.Url), ("target", target.Url),
                    ("status", target.NoIndex ? "marked noindex" : target.RedirectHops > 0 ? "redirects" : "HTTP " + target.StatusCode.ToString(System.Globalization.CultureInfo.InvariantCulture)))));

        // hreflang return links: A lists B, so B must list A.
        foreach (var page in html.Where(p => p.Alternates.Count > 0))
            foreach (var alternate in page.Alternates.Where(a => a != page.Url))
                if (byUrl.TryGetValue(alternate, out var other) && other.StatusCode == 200 && !other.Alternates.Contains(page.Url))
                {
                    findings.Add(new(page.Url, Finding.Fail("seo.hreflang", ("url", page.Url), ("target", other.Url))));
                    break;
                }

        // Sitemap URLs that were crawled must be 200, not redirected, indexable.
        foreach (var page in pages.Where(p => p.InSitemap))
            findings.Add(new(page.Url, page.StatusCode == 200 && page.RedirectHops == 0 && !page.NoIndex
                ? Finding.Pass("technical.sitemap")
                : Finding.Fail("technical.sitemap", ("n", 1), ("target", page.Url))));

        foreach (var page in pages.Where(p => p.LinkedInternally))
            findings.Add(new(page.Url, page.StatusCode is >= 200 and < 400
                ? Finding.Pass("technical.broken-internal-links")
                : Finding.Fail("technical.broken-internal-links", ("n", 1), ("target", page.Url), ("status", page.StatusCode == 0 ? "no answer" : page.StatusCode.ToString(System.Globalization.CultureInfo.InvariantCulture)), ("value", page.Referrer))));

        if (externalLinks is null)
            findings.Add(new(null, Finding.NotApplicable("technical.broken-external-links")));
        else
            foreach (var link in externalLinks)
                findings.Add(new(link.Url, link.Status is >= 200 and < 400 || link.Status is 401 or 403 or 405 or 429
                    ? Finding.Pass("technical.broken-external-links")
                    : Finding.Fail("technical.broken-external-links", ("n", 1), ("target", link.Url), ("status", link.Status?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? link.Error), ("value", link.Referrer))));

        foreach (var page in pages)
            findings.Add(new(page.Url, page.RedirectHops >= 2 ? Finding.Fail("technical.redirect-chains", ("n", 1), ("target", page.Url))
                : page.RedirectHops == 1 && page.FirstHopStatus is 302 or 303 or 307 ? Finding.Warn("technical.redirect-chains", ("n", 1), ("target", page.Url))
                : page.RedirectHops == 1 && page.LinkedInternally ? Finding.Warn("technical.redirect-chains", ("n", 1), ("target", page.Url))
                : Finding.Pass("technical.redirect-chains")));

        if (sitemapUrls.Count == 0)
            findings.Add(new(null, Finding.NotApplicable("technical.orphan-pages")));
        else
        {
            var linked = new HashSet<string>(pages.Where(p => p.LinkedInternally).Select(p => p.Url), StringComparer.Ordinal);
            foreach (var url in sitemapUrls)
                findings.Add(new(url, linked.Contains(url) || isLinked?.Invoke(url) == true ? Finding.Pass("technical.orphan-pages") : Finding.Fail("technical.orphan-pages", ("n", 1), ("target", url))));
        }

        foreach (var page in html.Where(p => p.Depth >= 0))
            findings.Add(new(page.Url, page.Depth > 3 ? Finding.Warn("technical.crawl-depth", ("n", 1), ("target", page.Url), ("value", page.Depth)) : Finding.Pass("technical.crawl-depth")));

        NearDuplicates(findings, html);
        return findings;
    }

    private static void Duplicates(List<CrawlFinding> findings, List<CrawledPage> html, Func<CrawledPage, string?> key, string id)
    {
        var groups = html.Where(p => !string.IsNullOrWhiteSpace(key(p)))
            .GroupBy(p => key(p)!.Trim().ToLowerInvariant(), StringComparer.Ordinal);
        foreach (var group in groups)
        {
            var members = group.ToList();
            foreach (var page in members)
                findings.Add(new(page.Url, members.Count > 1
                    ? Finding.Fail(id, ("k", members.Count), ("value", key(members[0])))
                    : Finding.Pass(id)));
        }
    }

    private static void NearDuplicates(List<CrawlFinding> findings, List<CrawledPage> html)
    {
        var prints = html.Where(p => p.Fingerprint is not null && p.Fingerprint != 0).ToList();
        var duplicateOf = new Dictionary<string, (int Count, string Example)>(StringComparer.Ordinal);
        if (prints.Count <= NearDuplicateLimit)
        {
            var neighbours = new int[prints.Count];
            var example = new string?[prints.Count];
            for (var i = 0; i < prints.Count; i++)
                for (var j = i + 1; j < prints.Count; j++)
                    if (BitOperations.PopCount(prints[i].Fingerprint!.Value ^ prints[j].Fingerprint!.Value) <= 3)
                    {
                        neighbours[i]++;
                        neighbours[j]++;
                        example[i] ??= prints[j].Url;
                        example[j] ??= prints[i].Url;
                    }
            for (var i = 0; i < prints.Count; i++)
                if (neighbours[i] > 0)
                    duplicateOf[prints[i].Url] = (neighbours[i] + 1, example[i]!);
        }
        else
            foreach (var group in prints.GroupBy(p => p.Fingerprint!.Value).Where(g => g.Count() > 1))
                foreach (var page in group)
                    duplicateOf[page.Url] = (group.Count(), group.First(p => p.Url != page.Url).Url);

        foreach (var page in prints)
            findings.Add(new(page.Url, duplicateOf.TryGetValue(page.Url, out var duplicate)
                ? Finding.Fail("technical.duplicate-content", ("k", duplicate.Count), ("target", duplicate.Example))
                : Finding.Pass("technical.duplicate-content")));
    }

    /// <summary>
    /// A 64-bit SimHash of <paramref name="text"/> over word 3-shingles (Charikar): near-identical texts differ in few
    /// bits. Empty or very short text gives 0, which is never matched.
    /// </summary>
    public static ulong Fingerprint(string text)
    {
        var words = text.ToLowerInvariant().Split((char[])[' ', '\n', '\t', '\r'], StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 20)
            return 0;
        Span<int> weights = stackalloc int[64];
        Span<byte> hash = stackalloc byte[32];
        for (var i = 0; i + 2 < words.Length && i < 20_000; i++)
        {
            var shingle = Encoding.UTF8.GetBytes(words[i] + " " + words[i + 1] + " " + words[i + 2]);
            SHA256.HashData(shingle, hash);
            var value = BitConverter.ToUInt64(hash);
            for (var bit = 0; bit < 64; bit++)
                weights[bit] += ((value >> bit) & 1) == 1 ? 1 : -1;
        }
        ulong print = 0;
        for (var bit = 0; bit < 64; bit++)
            if (weights[bit] > 0)
                print |= 1UL << bit;
        return print;
    }
}
