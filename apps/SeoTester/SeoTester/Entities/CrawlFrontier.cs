using MintPlayer.Spark.Abstractions;
using SeoTester.Checks;
using SeoTester.Crawling;

namespace SeoTester.Entities;

/// <summary>
/// The state of one run's crawl between batches (<c>docs/seo_analyzer_PRD.md</c> §4.2, §4.3): the URLs still to
/// fetch, the visited set, the robots.txt rules, the site-level findings of the probe and the counters. It is the
/// checkpoint of the page batches: a batch reads it, and its pages, the advanced frontier and the next message commit
/// together, so a batch that dies is simply done again. <c>@expires</c> 2 days; deleted when the run finishes.
/// Not a persistent object: only the pipeline reads it.
/// </summary>
public class CrawlFrontier : IExpiring
{
    public string? Id { get; set; }
    public string RunId { get; set; } = string.Empty;
    public string Origin { get; set; } = string.Empty;
    public string Host { get; set; } = string.Empty;

    /// <summary>URLs to fetch, in order (breadth first: the home page's links before deeper ones).</summary>
    public List<FrontierEntry> Pending { get; set; } = [];

    /// <summary><see cref="CrawlUrl.Hash"/> of every URL queued or fetched.</summary>
    public HashSet<string> Seen { get; set; } = [];

    /// <summary><see cref="CrawlUrl.Hash"/> of every URL some crawled page links to.</summary>
    public HashSet<string> Linked { get; set; } = [];

    /// <summary>The sitemap's same-site URLs the crawl kept (at most the page limit), for the orphan check.</summary>
    public List<string> SitemapUrls { get; set; } = [];

    /// <summary>The robots.txt rules that apply to the crawler (RFC 9309).</summary>
    public List<RobotsRule> RobotsRules { get; set; } = [];
    public double? CrawlDelaySeconds { get; set; }

    /// <summary>The batch the next <c>PageBatchMessage</c> must carry: an older one is a duplicate delivery.</summary>
    public int NextBatch { get; set; }

    public int Fetched { get; set; }
    public long Bytes { get; set; }
    public int Errors { get; set; }

    /// <summary>When the last request to the host started (politeness spans batches).</summary>
    public DateTime? LastRequestAtUtc { get; set; }

    /// <summary>Links to other sites, checked with HEAD at the end (verified sites only).</summary>
    public List<FrontierEntry> External { get; set; } = [];

    /// <summary>What the external links answered; null until they were checked (or when the run may not check them).</summary>
    public List<ExternalLinkResult>? ExternalResults { get; set; }

    /// <summary>The probe's site-level findings, scored by the aggregate.</summary>
    public List<StoredFinding> SiteFindings { get; set; } = [];

    /// <summary>Why the crawl stopped before the frontier was empty (page limit, wall time, the server asked to back off).</summary>
    public string? StopReason { get; set; }

    public DateTime? ExpiresAtUtc { get; set; }

    public static string IdFor(string runId) => runId + "/frontier";

    public RobotsTxt Robots() => RobotsTxt.FromRules(RobotsRules, CrawlDelaySeconds);
}

/// <param name="Depth">Clicks from the home page; -1 when the URL came from the sitemap only.</param>
/// <param name="Retries">How often the server already answered this URL with 429/503 and a Retry-After.</param>
public sealed record FrontierEntry(string Url, int Depth, string? Referrer, bool FromSitemap, int Retries = 0);

/// <summary>A <see cref="CheckFinding"/> as stored on the frontier.</summary>
public sealed record StoredFinding(string CheckId, string Status, string? Detail, string? Url = null)
{
    public static StoredFinding From(CheckFinding finding, string? url = null) => new(finding.CheckId, finding.Status.ToString(), finding.DetailJson, url);

    public CheckFinding ToFinding() => new(CheckId, Enum.TryParse<Scoring.CheckStatus>(Status, out var status) ? status : Scoring.CheckStatus.Error, CheckFinding.ParseDetail(Detail));
}

/// <summary>
/// A cached answer of a third-party lookup (RDAP, the IANA bootstrap, hstspreload.org): §4.2, natural ids
/// <c>LookupCaches/{kind}/{key}</c>, expiring after the kind's lifetime (1–7 days). Not a persistent object.
/// </summary>
public class LookupCache : IExpiring
{
    public string? Id { get; set; }
    public string Kind { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;

    /// <summary>The answer (JSON as received, capped), or null for "nothing there" (a 404).</summary>
    public string? Value { get; set; }
    public int Status { get; set; }
    public DateTime FetchedAtUtc { get; set; }
    public DateTime? ExpiresAtUtc { get; set; }

    public static string IdFor(string kind, string key) => $"LookupCaches/{kind}/{key.ToLowerInvariant()}";
}
