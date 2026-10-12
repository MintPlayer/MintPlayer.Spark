using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Messaging;
using MintPlayer.Spark.Messaging.Abstractions;
using Raven.Client.Documents.Session;
using SeoTester.Checks;
using SeoTester.Crawler;
using SeoTester.Entities;
using SeoTester.LookupReferences;
using SeoTester.Messaging;
using SeoTester.Rendering;
using SeoTester.Scoring;

namespace SeoTester.PageSpeed;

/// <summary>
/// <c>seo-psi</c> (<c>docs/seo_analyzer_PRD.md</c> §4.3, F13, D9, spike S5): measures the run's sample (the tier's
/// <c>PsiSample</c>: the home page, verified sites up to 5) with PageSpeed Insights, one page at a time. Without a key
/// both checks are "not measured: PageSpeed Insights is not configured" (never a failure, never an anonymous call: its
/// quota is 0, S5). A 429 is Google's quota, not the site's fault: the stage asks again later (<c>QuotaRetries</c>
/// times, <c>QuotaRetryDelay</c> × attempt) and then records the pages "not measured". Findings, the stage mark and the
/// aggregate's message commit together.
/// </summary>
public partial class PsiRecipient : IRecipient<PsiMessage>
{
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly IMessageOutbox outbox;
    [Inject] private readonly IMessageContext context;
    [Inject] private readonly RunPipeline pipeline;
    [Inject] private readonly PageSpeedClient client;
    [Inject] private readonly ILogger<PsiRecipient> logger;

    public async Task HandleAsync(PsiMessage message, CancellationToken cancellationToken = default)
    {
        if (await RunPipeline.LoadActiveAsync(session, message.RunId, cancellationToken) is not { } run || run.StagesDone.Contains(PipelineStages.Psi))
            return;
        if (run.CancelRequested)
        {
            await pipeline.EndAsync(run, AuditRunStatus.Cancelled, null, null, cancellationToken);
            return;
        }
        await pipeline.GuardAsync(run, context, () => MeasureAsync(run, message.Attempt, cancellationToken), cancellationToken);
    }

    private async Task MeasureAsync(AuditRun run, int attempt, CancellationToken cancellationToken)
    {
        var pages = new Dictionary<string, List<PageFinding>>(StringComparer.Ordinal);
        var site = new List<StoredFinding>();
        var frontier = await session.LoadAsync<CrawlFrontier>(CrawlFrontier.IdFor(run.Id!), cancellationToken);
        if (frontier is not null)
            session.Advanced.Evict(frontier);
        var sample = await RenderSample.PickAsync(session, run, frontier, run.PsiSample, cancellationToken);
        if (!client.Configured)
            NotMeasured(site, "PageSpeed Insights is not configured");
        else if (sample.Count == 0)
            NotMeasured(site, "no page answered 200");
        else
        {
            foreach (var page in sample)
            {
                var outcome = await client.MeasureAsync(new Uri(page.Url), cancellationToken);
                if (outcome.Kind == PsiOutcomeKind.QuotaExhausted)
                {
                    if (attempt < client.Options.QuotaRetries)
                    {
                        // Nothing of this attempt is stored: measured pages are cached, so the next attempt does not ask again.
                        logger.LogWarning("PageSpeed Insights quota exhausted for {RunId}; asking again later (attempt {Attempt})", run.Id, attempt + 1);
                        await outbox.EnqueueAsync(session, new PsiMessage(run.Id!, attempt + 1),
                            new BroadcastOptions { Delay = client.Options.QuotaRetryDelay * (attempt + 1) }, cancellationToken);
                        await session.SaveChangesAsync(cancellationToken);
                        return;
                    }
                    pages[page.Url] = [.. PsiChecks.Ids.Select(id => Stored(PsiChecks.NotMeasured(id, "the PageSpeed Insights quota is exhausted")))];
                    continue;
                }
                pages[page.Url] = outcome.Summary is { } summary
                    ? [.. PsiChecks.Evaluate(summary with { Url = page.Url }).Select(Stored)]
                    : [.. PsiChecks.Ids.Select(id => Stored(Finding.Error(id, "PageSpeed Insights could not test the page: " + outcome.Reason)))];
            }
        }

        RunPipeline.AddFindings(session, run, pages, site);
        await pipeline.FinishStageAsync(session, outbox, run.Id!, PipelineStages.Psi, cancellationToken);
        await session.SaveChangesAsync(cancellationToken);
    }

    private static PageFinding Stored(CheckFinding f) => new() { CheckId = f.CheckId, Status = f.Status.ToString(), Detail = f.DetailJson };

    private static void NotMeasured(List<StoredFinding> site, string reason)
    {
        foreach (var id in PsiChecks.Ids)
            site.Add(StoredFinding.From(PsiChecks.NotMeasured(id, reason)));
    }
}
