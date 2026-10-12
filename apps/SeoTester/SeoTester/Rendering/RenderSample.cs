using System.Text.RegularExpressions;
using Raven.Client.Documents.Session;
using SeoTester.Entities;

namespace SeoTester.Rendering;

/// <summary>
/// Which crawled pages the render and PageSpeed stages measure (<c>docs/seo_analyzer_PRD.md</c> §6: 3 / 10 rendered, 1 /
/// 5 PageSpeed): HTML pages that answered 200, the home page first, then the shallowest, one per URL template
/// (<c>/blog/a</c> and <c>/blog/b</c> are one template) before a second of any template (research §3.2: "one per URL
/// template").
/// </summary>
public static partial class RenderSample
{
    /// <summary>The sample among the pages <paramref name=run/> stored (<see cref="Crawler.RunPipeline.LoadRunPagesAsync"/>).</summary>
    public static async Task<List<PageResult>> PickAsync(IAsyncDocumentSession session, AuditRun run, CrawlFrontier? frontier, int count, CancellationToken cancellationToken)
        => count <= 0 ? [] : Pick(await Crawler.RunPipeline.LoadRunPagesAsync(session, run, frontier, cancellationToken), count);

    public static List<PageResult> Pick(IEnumerable<PageResult> pages, int count)
    {
        var candidates = pages
            .Where(p => p.StatusCode == 200 && p.Fingerprint is not null && p.FinalUrl is null && Uri.TryCreate(p.Url, UriKind.Absolute, out _))
            .OrderBy(p => p.Depth < 0 ? int.MaxValue : p.Depth)
            .ThenBy(p => p.Url.Length)
            .ThenBy(p => p.Url, StringComparer.Ordinal)
            .ToList();
        var picked = new List<PageResult>();
        var templates = new HashSet<string>(StringComparer.Ordinal);
        foreach (var page in candidates)
            if (picked.Count < count && templates.Add(Template(new Uri(page.Url))))
                picked.Add(page);
        foreach (var page in candidates)
            if (picked.Count < count && !picked.Contains(page))
                picked.Add(page);
        return picked;
    }

    /// <summary>The path with digits generalised and, below the first level, its last segment dropped: <c>/blog/*</c>.</summary>
    public static string Template(Uri url)
    {
        var segments = url.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(s => Digits().Replace(s, "#")).ToList();
        return segments.Count <= 1 ? "/" + string.Join('/', segments) : "/" + string.Join('/', segments.Take(segments.Count - 1)) + "/*";
    }

    [GeneratedRegex("[0-9]+")]
    private static partial Regex Digits();
}
