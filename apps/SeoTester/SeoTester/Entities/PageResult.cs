using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Organizations;

namespace SeoTester.Entities;

/// <summary>
/// One crawled page of a run (<c>docs/seo_analyzer_PRD.md</c> F6, §4.2): expires 7 days after it was written (D8,
/// <c>expiresAfter</c> in the model; X1 hides it from every read the moment it is due, before RavenDB's sweep). Its
/// id is <c>{runId}/pages/{urlHash}</c>, so re-crawling a page overwrites it (M9). Shown as a sub-query of its run.
/// </summary>
public class PageResult : IOrganizationOwned, IExpiring
{
    public string? Id { get; set; }

    [Reference(typeof(AuditRun))]
    public string? RunId { get; set; }

    /// <summary>The run's organization: the page is read under the same rule as its run.</summary>
    public string? OrganizationId { get; set; }

    public string Url { get; set; } = string.Empty;
    public int StatusCode { get; set; }
    public int Depth { get; set; }

    /// <summary>Time to the full response, in milliseconds.</summary>
    public int? LoadMs { get; set; }

    // The previewers' snapshot (F7).
    public string? Title { get; set; }
    public string? Description { get; set; }
    public string? H1 { get; set; }

    // The page's Open Graph tags as written (Q-M11d), for the shared-link card. They expire with the page. The image is
    // never loaded by the viewer's browser from the audited site: the card shows it through <c>/seotester/og-image</c>,
    // fetched by the server over the SSRF-safe client with a size and type cap (<c>Reports/OgImageProxy</c>).
    public string? OgTitle { get; set; }
    public string? OgDescription { get; set; }
    public string? OgImage { get; set; }
    public string? OgUrl { get; set; }

    public List<PageFinding> Findings { get; set; } = [];

    public DateTime? ExpiresAtUtc { get; set; }

    // ---- What the aggregate reads (M9): the crawl-level checks (duplicates, link graph, redirects, sitemap,
    // hreflang return links, near-duplicates). Server-only, not model attributes. ------------------------------------

    /// <summary>Where the URL ended after redirects; null when it did not redirect.</summary>
    [IgnoreProperty]
    public string? FinalUrl { get; set; }

    [IgnoreProperty]
    public int RedirectHops { get; set; }

    [IgnoreProperty]
    public int? FirstHopStatus { get; set; }

    /// <summary>The page's canonical URL (absolute), when it declares exactly one.</summary>
    [IgnoreProperty]
    public string? Canonical { get; set; }

    /// <summary>The hreflang alternates' URLs (at most 50).</summary>
    [IgnoreProperty]
    public List<string> Alternates { get; set; } = [];

    /// <summary>The first page found linking here (internal links), for "broken link on page …".</summary>
    [IgnoreProperty]
    public string? Referrer { get; set; }

    [IgnoreProperty]
    public bool InSitemap { get; set; }

    [IgnoreProperty]
    public bool LinkedInternally { get; set; }

    [IgnoreProperty]
    public bool NoIndex { get; set; }

    /// <summary>The visible text's 64-bit SimHash, hex; null for anything but a 200 HTML page.</summary>
    [IgnoreProperty]
    public string? Fingerprint { get; set; }

    /// <summary>The prefix every page id of <paramref name="runId"/> starts with (read by prefix, no index).</summary>
    public static string PrefixFor(string runId) => $"{runId}/pages/";

    /// <summary>The id of the page <paramref name="url"/> of run <paramref name="runId"/>: a hash, so any URL makes a valid id.</summary>
    public static string IdFor(string runId, string url)
        => $"{runId}/pages/{Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(url)))[..32]}";
}

/// <summary>A check's result on one page. Texts are rendered from the catalogue and <see cref="Detail"/>, never stored.</summary>
[ValueObject]
public partial class PageFinding
{
    [ValueKey]
    public string CheckId { get; set; } = string.Empty;

    /// <summary>Pass, Warn, Fail, NotApplicable or Error.</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>The template parameters (<c>{n}</c>, <c>{value}</c>, …) as a short JSON object.</summary>
    public string? Detail { get; set; }
}
