using SeoTester.Crawling;
using SeoTester.Entities;

namespace SeoTester.Crawler;

/// <summary>How a crawled page extends the frontier (breadth first, bounded).</summary>
public static class CrawlFrontiers
{
    /// <summary>Most URLs queued at once: a little over the page limit, so URLs skipped by robots.txt do not starve the crawl.</summary>
    public static int QueueLimit(int maxPages) => maxPages + Math.Max(50, maxPages / 2);

    private static readonly string[] NotPages =
    [
        ".pdf", ".jpg", ".jpeg", ".png", ".gif", ".webp", ".avif", ".svg", ".ico", ".bmp", ".tif", ".tiff", ".mp4", ".webm", ".mov", ".avi",
        ".mp3", ".wav", ".ogg", ".zip", ".gz", ".tgz", ".rar", ".7z", ".exe", ".msi", ".dmg", ".iso", ".apk", ".woff", ".woff2", ".ttf", ".otf",
        ".eot", ".css", ".js", ".mjs", ".map", ".json", ".xml", ".rss", ".atom", ".txt", ".csv", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx",
    ];

    /// <summary>Whether a URL is worth fetching as a page (files and assets are not crawled).</summary>
    public static bool LooksLikePage(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) && !NotPages.Any(e => uri.AbsolutePath.EndsWith(e, StringComparison.OrdinalIgnoreCase));

    /// <summary>Queues the page's unseen internal links one level deeper, marks every link as linked, and keeps external links to check.</summary>
    public static void Extend(CrawlFrontier frontier, AnalyzedPage page, int maxQueued, int maxExternal)
    {
        var depth = page.Result.Depth < 0 ? 1 : page.Result.Depth + 1;
        var limit = QueueLimit(maxQueued);
        foreach (var link in page.InternalLinks)
        {
            var url = link.AbsoluteUri;
            var hash = CrawlUrl.Hash(url);
            frontier.Linked.Add(hash);
            if (frontier.Seen.Count >= limit || !LooksLikePage(url))
                continue;
            if (frontier.Seen.Add(hash))
                frontier.Pending.Add(new FrontierEntry(url, depth, page.Result.Url, FromSitemap: false));
        }
        if (maxExternal <= 0)
            return;
        foreach (var link in page.ExternalLinks)
        {
            if (frontier.External.Count >= maxExternal)
                break;
            if (frontier.External.All(e => !string.Equals(new Uri(e.Url).IdnHost, link.IdnHost, StringComparison.OrdinalIgnoreCase)))
                frontier.External.Add(new FrontierEntry(link.AbsoluteUri, 0, page.Result.Url, FromSitemap: false));
        }
    }
}
