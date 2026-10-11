using System.Diagnostics;
using System.Security.Claims;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Messaging.Abstractions;
using MintPlayer.Spark.Notifications;
using MintPlayer.Spark.Organizations;
using Microsoft.Playwright;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;
using SeoTester.Catalogue;
using SeoTester.Entities;
using SeoTester.LookupReferences;
using SeoTester.Messaging;
using SeoTester.Rendering;

namespace SeoTester.Reports;

/// <summary>
/// A run's report printed as PDF (<c>docs/seo_analyzer_PRD.md</c> F14, M10), one per run and language: the PDF is an
/// attachment of this document, which expires after <see cref="Retention"/> (§4.2: storage stays modest; the report can
/// always be printed again from the run, which is kept). Not a persistent object: read through
/// <see cref="ReportPdfEndpoint"/> only.
/// </summary>
public class RunReport : IExpiring
{
    public const string AttachmentName = "report.pdf";

    /// <summary>How long a printed report is kept.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(7);

    public string? Id { get; set; }
    public string RunId { get; set; } = string.Empty;
    public string? OrganizationId { get; set; }
    public string Culture { get; set; } = "en";
    public long Bytes { get; set; }
    public int RenderMs { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? ExpiresAtUtc { get; set; }

    public static string IdFor(string runId, string culture) => $"{runId}/reports/{ReportPage.CultureOf(culture)}";

    /// <summary>Where the PDF is downloaded (signed-in members of the run's organization).</summary>
    public static string DownloadUrl(string runId, string culture)
        => $"{ReportPdfEndpoint.Route}?run={Uri.EscapeDataString(runId)}&culture={ReportPage.CultureOf(culture)}";
}

/// <summary>
/// <c>seo-pdf</c> (§4.3): prints the report page (<see cref="ReportPage"/>) in Chromium (<c>page.PdfAsync</c>, A4, the
/// print CSS) in a context where every request is refused (the page needs none), stores it, and tells whoever asked.
/// </summary>
public partial class PdfRecipient : IRecipient<PdfMessage>
{
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly ChromiumBrowser chromium;
    [Inject] private readonly ISparkNotifier notifier;
    [Inject] private readonly TimeProvider time;
    [Inject] private readonly ILogger<PdfRecipient> logger;

    public async Task HandleAsync(PdfMessage message, CancellationToken cancellationToken = default)
    {
        var run = await session.LoadAsync<AuditRun>(message.RunId, cancellationToken);
        if (run is null || run.Status != AuditRunStatus.Completed || !chromium.Enabled)
            return;
        var culture = ReportPage.CultureOf(message.Culture);
        var id = RunReport.IdFor(run.Id!, culture);
        var now = time.GetUtcNow().UtcDateTime;
        if (await session.LoadAsync<RunReport>(id, cancellationToken) is not { ExpiresAtUtc: { } until } || until <= now)
        {
            var definitions = await session.Query<CheckDefinition>().Take(1024).ToListAsync(cancellationToken);
            var html = ReportPage.Render(run, definitions.GroupBy(d => d.CheckId).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal), culture, now);
            var watch = Stopwatch.StartNew();
            var pdf = await PrintAsync(html, cancellationToken);
            var report = new RunReport
            {
                Id = id,
                RunId = run.Id!,
                OrganizationId = run.OrganizationId,
                Culture = culture,
                Bytes = pdf.LongLength,
                RenderMs = (int)watch.ElapsedMilliseconds,
                CreatedAtUtc = now,
                ExpiresAtUtc = now + RunReport.Retention,
            };
            await session.StoreAsync(report, cancellationToken);
            session.Advanced.Attachments.Store(id, RunReport.AttachmentName, new MemoryStream(pdf), "application/pdf");
            await session.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Printed the {Culture} report of {RunId}: {Bytes} bytes in {Ms} ms", culture, run.Id, pdf.Length, report.RenderMs);
        }

        if (message.RequestedBy is { } user)
        {
            try
            {
                await notifier.NotifyAsync(new SparkNotification
                {
                    UserIds = [user],
                    Title = culture == "nl" ? $"Het rapport van {run.Origin} staat klaar" : $"The report of {run.Origin} is ready",
                    Body = culture == "nl" ? "Open de melding om de PDF te downloaden." : "Open the notification to download the PDF.",
                    Url = RunReport.DownloadUrl(run.Id!, culture),
                    OrganizationId = run.OrganizationId,
                    Category = "report-ready",
                }, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Telling {User} about the report of {RunId} failed", user, run.Id);
            }
        }
    }

    private async Task<byte[]> PrintAsync(string html, CancellationToken cancellationToken)
    {
        var browser = await chromium.GetAsync(cancellationToken);
        var context = await browser.NewContextAsync(new BrowserNewContextOptions { JavaScriptEnabled = false, ServiceWorkers = ServiceWorkerPolicy.Block });
        try
        {
            // The report is self-contained: any request at all is refused.
            await context.RouteAsync("**/*", route => route.AbortAsync("blockedbyclient"));
            var page = await context.NewPageAsync();
            await page.SetContentAsync(html, new PageSetContentOptions { WaitUntil = WaitUntilState.Load, Timeout = 30_000 });
            return await page.PdfAsync(new PagePdfOptions { Format = "A4", PrintBackground = true, PreferCSSPageSize = true });
        }
        finally
        {
            try
            {
                await context.CloseAsync();
            }
            catch (PlaywrightException)
            {
            }
        }
    }
}

/// <summary>
/// <c>ExportReport</c> on a finished run (F14; Viewers of the owning organization and up: reading the report is what a
/// Viewer may do, D23). A printed report of the caller's language is offered at once; otherwise <c>seo-pdf</c> prints it
/// and the caller is notified with the link.
/// </summary>
public partial class ExportReportAction : SparkCustomAction
{
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;
    [Inject] private readonly IManager manager;
    [Inject] private readonly MintPlayer.Spark.Services.IRequestCultureResolver cultures;
    [Inject] private readonly ChromiumBrowser chromium;
    [Inject] private readonly MintPlayer.Spark.Messaging.IMessageOutbox outbox;
    [Inject] private readonly TimeProvider time;

    public override async Task ExecuteAsync(MintPlayer.Spark.Abstractions.Actions.CustomActionArgs args, CancellationToken cancellationToken)
    {
        var run = args.Parent?.Id is { Length: > 0 } id ? await session.LoadAsync<AuditRun>(id, cancellationToken) : null;
        if (run is null)
            throw new SparkValidationException("Open the audit first.");
        if (run.Status != AuditRunStatus.Completed)
            throw new SparkValidationException(manager.GetTranslatedMessage("seo.reportNeedsFinishedRun", run.Origin));
        if (!chromium.Enabled)
            throw new SparkValidationException(manager.GetTranslatedMessage("seo.reportUnavailable"));

        var culture = ReportPage.CultureOf(cultures.GetCurrentCulture());
        var url = RunReport.DownloadUrl(run.Id!, culture);
        if (await session.LoadAsync<RunReport>(RunReport.IdFor(run.Id!, culture), cancellationToken) is { ExpiresAtUtc: { } until } && until > time.GetUtcNow().UtcDateTime)
        {
            args.SetResult(new { url, ready = true });
            return;
        }
        var userId = httpContextAccessor.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier);
        await outbox.EnqueueAsync(session, new PdfMessage(run.Id!, culture, userId), cancellationToken: cancellationToken);
        await session.SaveChangesAsync(cancellationToken);
        args.SetResult(new { url, ready = false });
        manager.Client.Notify(manager.GetTranslatedMessage("seo.reportBeingPrinted", run.Origin), MintPlayer.Spark.Abstractions.ClientOperations.NotificationKind.Info);
    }
}

/// <summary>
/// <c>GET /seotester/report.pdf?run=…&amp;culture=en|nl</c>: a printed report, for signed-in members (any role) of the
/// run's organization. Anyone else gets the same 404 as for a report that does not exist (#453: no existence oracle).
/// </summary>
internal sealed partial class ReportPdfEndpoint : IGetEndpoint
{
    public const string Route = "/seotester/report.pdf";

    public static string Path => Route;

    [Inject] private readonly IDocumentStore store;
    [QueryParam("run")] public string? RunId { get; set; }
    [QueryParam] public string? Culture { get; set; }

    [Inject] private readonly ISparkOrganizations organizations;
    [Inject] private readonly TimeProvider time;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        var ct = httpContext.RequestAborted;
        var userId = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);
        var runId = RunId ?? string.Empty;
        if (userId is null || runId.Length is 0 or > 200)
            return Results.NotFound();
        var culture = ReportPage.CultureOf(Culture);
        using var session = store.OpenAsyncSession();
        var report = await session.LoadAsync<RunReport>(RunReport.IdFor(runId, culture), ct);
        if (report is null || report.RunId != runId || report.ExpiresAtUtc <= time.GetUtcNow().UtcDateTime
            || report.OrganizationId is null || await organizations.GetRoleAsync(report.OrganizationId, userId, ct) is null)
            return Results.NotFound();
        var attachment = await session.Advanced.Attachments.GetAsync(report.Id!, RunReport.AttachmentName, ct);
        if (attachment is null)
            return Results.NotFound();
        using var buffer = new MemoryStream();
        await attachment.Stream.CopyToAsync(buffer, ct);
        attachment.Dispose();
        httpContext.Response.Headers.CacheControl = "private, no-store";
        return Results.File(buffer.ToArray(), "application/pdf", $"seotester-report-{culture}.pdf");
    }
}
