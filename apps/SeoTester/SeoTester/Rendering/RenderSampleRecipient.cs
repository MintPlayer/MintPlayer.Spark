using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Messaging;
using MintPlayer.Spark.Messaging.Abstractions;
using MintPlayer.Spark.Settings;
using Microsoft.Playwright;
using Raven.Client.Documents.Session;
using SeoTester.Checks;
using SeoTester.Crawler;
using SeoTester.Entities;
using SeoTester.Fetching;
using SeoTester.LookupReferences;
using SeoTester.Messaging;
using SeoTester.Scoring;
using SeoTester.Settings;

namespace SeoTester.Rendering;

/// <summary>
/// <c>seo-render</c> (<c>docs/seo_analyzer_PRD.md</c> §4.3, F12, M10): renders the run's sample (<see cref="RenderSample"/>,
/// the tier's <c>RenderedSample</c> pages) in Chromium, one render at a time across the process
/// (<c>ConcurrentRenders</c>), every browser request fetched through the SSRF-safe client politely
/// (<see cref="RenderNetwork"/>). The findings go onto the sampled pages and, once per run, the frontier; with the stage
/// mark and the aggregate's message in one commit. Without a browser (<c>SeoTester:Browser:Mode</c> Off) or when it
/// cannot be reached, the rendered checks are "not measured" (excluded from the score, §7) and the run goes on.
/// </summary>
public partial class RenderSampleRecipient : IRecipient<RenderSampleMessage>
{
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly IMessageOutbox outbox;
    [Inject] private readonly IMessageContext context;
    [Inject] private readonly RunPipeline pipeline;
    [Inject] private readonly ChromiumBrowser chromium;
    [Inject] private readonly PageRenderer renderer;
    [Inject] private readonly RenderGate gate;
    [Inject] private readonly ISparkSettings settings;
    [Inject] private readonly ISeoFetcher fetcher;
    [Inject] private readonly TimeProvider time;
    [Inject] private readonly ILogger<RenderSampleRecipient> logger;

    public async Task HandleAsync(RenderSampleMessage message, CancellationToken cancellationToken = default)
    {
        if (await RunPipeline.LoadActiveAsync(session, message.RunId, cancellationToken) is not { } run || run.StagesDone.Contains(PipelineStages.Render))
            return;
        if (run.CancelRequested)
        {
            await pipeline.EndAsync(run, AuditRunStatus.Cancelled, null, null, cancellationToken);
            return;
        }
        await pipeline.GuardAsync(run, context, () => RenderAsync(run, cancellationToken), cancellationToken);
    }

    private async Task RenderAsync(AuditRun run, CancellationToken cancellationToken)
    {
        var pages = new Dictionary<string, List<PageFinding>>(StringComparer.Ordinal);
        var site = new List<StoredFinding>();
        var sample = await RenderSample.PickAsync(session, run.Id!, run.RenderedSample, cancellationToken);
        var frontier = await session.LoadAsync<CrawlFrontier>(CrawlFrontier.IdFor(run.Id!), cancellationToken);
        if (frontier is not null)
            session.Advanced.Evict(frontier);
        var pwa = run.SiteId is not null && (await session.LoadAsync<Site>(run.SiteId, cancellationToken))?.PwaProfile == true;

        if (!chromium.Enabled)
            NotMeasured(site, "the headless browser is not configured");
        else if (sample.Count == 0 || frontier is null)
            NotMeasured(site, "no page could be rendered (no HTML page answered 200)");
        else
        {
            RunPipeline.PatchProgress(session, run.Id!, "render", run.PagesDone, run.PagesDiscovered, null);
            await session.SaveChangesAsync(cancellationToken);
            try
            {
                var concurrent = await settings.GetAsync(SeoSettings.ConcurrentRenders, cancellationToken);
                using (await gate.EnterAsync(() => concurrent, cancellationToken))
                    await RenderSampleAsync(run, frontier, sample, pwa, pages, site, cancellationToken);
            }
            catch (PlaywrightException ex)
            {
                // The browser (container) cannot be reached: not the site's fault, and the rest of the report stands.
                logger.LogWarning(ex, "The headless browser is unavailable for {RunId}", run.Id);
                pages.Clear();
                site.Clear();
                foreach (var id in RenderChecks.Ids)
                    site.Add(StoredFinding.From(Finding.Error(id, "the headless browser was unavailable")));
            }
        }

        RunPipeline.AddFindings(session, run.Id!, pages, site);
        await pipeline.FinishStageAsync(session, outbox, run.Id!, PipelineStages.Render, cancellationToken);
        await session.SaveChangesAsync(cancellationToken);
    }

    private async Task RenderSampleAsync(AuditRun run, CrawlFrontier frontier, List<PageResult> sample, bool pwa,
        Dictionary<string, List<PageFinding>> pages, List<StoredFinding> site, CancellationToken cancellationToken)
    {
        var origin = new Uri(run.Origin);
        var interval = await pipeline.IntervalAsync(run, frontier.CrawlDelaySeconds, cancellationToken);
        using var pacer = new HostPacer(run.ConnectionsPerHost, interval, frontier.LastRequestAtUtc, time);
        using var network = new RenderNetwork(fetcher, origin, frontier.Robots(), pacer, logger);

        string? serviceWorker = null;
        (bool RouteChanged, int Hits)? probe = null;
        var hitsOnLoad = 0;
        for (var i = 0; i < sample.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = sample[i];
            var render = await renderer.RenderAsync(network, new Uri(page.Url), probeRoute: i == 0, cancellationToken);
            logger.LogInformation("Run {RunId}: rendered {Url} ({Timings})", run.Id, page.Url,
                string.Join(", ", render.Timings.Select(t => $"{t.Key} {t.Value} ms")));
            pages[page.Id!] = RenderChecks.Evaluate(render.Facts with { Url = page.Url })
                .Where(f => f.Status != CheckStatus.NotApplicable)
                .Select(f => new PageFinding { CheckId = f.CheckId, Status = f.Status.ToString(), Detail = f.DetailJson })
                .ToList();
            serviceWorker ??= render.ServiceWorkerUrl;
            if (i == 0)
            {
                probe = render.RouteProbe;
                hitsOnLoad = render.HitsOnLoad;
            }
            await pipeline.WriteProgressAsync(run.Id!, "render", run.PagesDone, run.PagesDiscovered, page.Url, cancellationToken);
        }

        var worker = serviceWorker is null ? null : await PageRenderer.ServiceWorkerAsync(network, serviceWorker, cancellationToken);
        var analytics = network.Tools.Count == 0 ? null
            : new AnalyticsFact(network.Tools, hitsOnLoad, network.ConsentManager, probe?.RouteChanged, probe?.Hits ?? 0);
        site.AddRange(RenderChecks.EvaluateSite(worker, analytics, pwa).Select(f => StoredFinding.From(f)));
    }

    private static void NotMeasured(List<StoredFinding> site, string reason)
    {
        foreach (var id in RenderChecks.Ids)
            site.Add(StoredFinding.From(Finding.Make(id, CheckStatus.NotApplicable, [("reason", reason)])));
    }
}
