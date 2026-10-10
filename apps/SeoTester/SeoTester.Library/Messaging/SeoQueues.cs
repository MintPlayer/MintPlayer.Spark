using MintPlayer.Spark.Messaging.Abstractions;

namespace SeoTester.Messaging;

/// <summary>
/// The crawl pipeline's queues (<c>docs/seo_analyzer_PRD.md</c> §4.3). Their concurrency and handler timeouts are
/// configuration (<c>Spark:Messaging:Queues:{name}</c> in <c>appsettings.json</c>), so an operator can change them
/// without a build. The recipients are M9's (probe, crawl, aggregate) and M10's (render, PSI, PDF).
/// </summary>
public static class SeoQueues
{
    /// <summary>robots.txt, sitemaps, TLS, DNS, RDAP and the other site-level probes; seeds the frontier.</summary>
    public const string Probe = "seo-probe";

    /// <summary>Batches of pages fetched through the SSRF-safe client (X2), chained until the run is done.</summary>
    public const string Crawl = "seo-crawl";

    /// <summary>The headless-browser sample (Chromium container).</summary>
    public const string Render = "seo-render";

    /// <summary>PageSpeed Insights / CrUX on the sample.</summary>
    public const string Psi = "seo-psi";

    /// <summary>Site-level checks, scoring, deltas and alerts, once every stage finished.</summary>
    public const string Aggregate = "seo-aggregate";

    /// <summary>The report as PDF, printed by the Chromium container.</summary>
    public const string Pdf = "seo-pdf";

    /// <summary>Every queue above, in pipeline order.</summary>
    public static IReadOnlyList<string> All { get; } = [Probe, Crawl, Render, Psi, Aggregate, Pdf];
}

/// <summary>Starts a run: the site-level probes (§4.3).</summary>
[MessageQueue(SeoQueues.Probe)]
public sealed record SiteProbeMessage(string RunId);

/// <summary>One batch of pages of a run; <paramref name="BatchNo"/> makes the batch idempotent.</summary>
[MessageQueue(SeoQueues.Crawl)]
public sealed record PageBatchMessage(string RunId, int BatchNo);

/// <summary>Renders the run's sample in Chromium.</summary>
[MessageQueue(SeoQueues.Render)]
public sealed record RenderSampleMessage(string RunId);

/// <summary>Measures the run's sample with PageSpeed Insights / CrUX; <paramref name="Attempt"/> counts the waits after a 429 (quota).</summary>
[MessageQueue(SeoQueues.Psi)]
public sealed record PsiMessage(string RunId, int Attempt = 0);

/// <summary>Scores the run once every stage finished.</summary>
[MessageQueue(SeoQueues.Aggregate)]
public sealed record AggregateMessage(string RunId);

/// <summary>Prints the run's report as PDF in <paramref name="Culture"/> (en or nl) for <paramref name="RequestedBy"/>, who is notified.</summary>
[MessageQueue(SeoQueues.Pdf)]
public sealed record PdfMessage(string RunId, string Culture, string? RequestedBy = null);
