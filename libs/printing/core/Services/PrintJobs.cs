using System.Text;
using Microsoft.Extensions.Options;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Printing.Indexes;
using MintPlayer.Spark.Printing.Models;
using MintPlayer.Spark.Printing.Rendering.Labels;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Printing.Services;

/// <summary>What to print: a label template (rendered for the printer), a PDF document, or a carrier's ready bytes.</summary>
public abstract record PrintContent
{
    private PrintContent() { }

    /// <summary>A label: ZPL at the printer's resolution, or a PDF of the label's size; a ZPL job keeps a PDF for the fallback.</summary>
    public sealed record Label(ILabelTemplate Template) : PrintContent;

    /// <summary>A PDF document (a packing slip, a pick list, an invoice): PDF printers only.</summary>
    public sealed record Document(byte[] Pdf) : PrintContent;

    /// <summary>
    /// Bytes a carrier returned (PRD §4.11: carrier bytes with their media type): <c>application/pdf</c>, or
    /// <c>application/zpl</c> at <paramref name="Dpi"/>. <paramref name="FallbackPdf"/> is printed when the agent is down.
    /// </summary>
    public sealed record Carrier(string ContentType, byte[] Bytes, int? Dpi = null, byte[]? FallbackPdf = null) : PrintContent;
}

/// <summary>A job to queue.</summary>
/// <param name="Key">The caller's idempotency key: the same key queues one job (a retried ship-confirm prints once).</param>
/// <param name="Printer">A printer by name; null routes by <paramref name="Kind"/>, <paramref name="Site"/> and <paramref name="Station"/>.</param>
public sealed record PrintRequest(string Key, string Kind, string Title, PrintContent Content, string? Site = null, string? Station = null,
    string? Printer = null, string? UserId = null);

public enum PrintSubmitOutcome
{
    Queued,
    /// <summary>A job with this key exists already; nothing was queued again.</summary>
    Existing,
    /// <summary>No printer routes the job (no match and no default).</summary>
    NoPrinter,
    /// <summary>The printer cannot print this content (an A4 PDF on a ZPL printer, ZPL of another resolution).</summary>
    Unprintable,
}

public sealed record PrintSubmitResult(PrintSubmitOutcome Outcome, string? JobId, string? Printer = null, string? Detail = null)
{
    public bool Succeeded => Outcome is PrintSubmitOutcome.Queued or PrintSubmitOutcome.Existing;
}

/// <summary>A job handed to an agent or a station.</summary>
public sealed record PrintDelivery(string JobId, string Printer, string? Address, string? DeviceName, string ContentType, int? Dpi, byte[] Content, string Title);

public enum PrintAckOutcome
{
    Recorded,
    /// <summary>Acknowledged before; nothing changed (a repeated acknowledgement after a lost answer).</summary>
    AlreadyRecorded,
    /// <summary>Not this agent's or station's job, or no such job: the caller learns nothing about it.</summary>
    NotFound,
}

/// <summary>
/// Print jobs (PRD §4.11): queued idempotently by key, routed to a printer, delivered to the on-site agent or the station's
/// browser, acknowledged exactly once. Delivery is at least once (a delivered job not acknowledged in
/// <c>RedeliverAfter</c> goes out again) and the agent's journal turns a repeat into an acknowledgement without printing,
/// so each job id prints once (W16). When an agent is offline, its station is offered the job's PDF (WD7).
/// </summary>
public interface IPrintJobs
{
    Task<PrintSubmitResult> SubmitAsync(PrintRequest request, CancellationToken cancellationToken = default);

    /// <summary>The agent's next jobs (queued, or delivered and unacknowledged too long), marked delivered.</summary>
    Task<IReadOnlyList<PrintDelivery>> PullAsync(string agent, int max = 20, CancellationToken cancellationToken = default);

    /// <summary>The agent says it printed (or could not print) a job. Only that agent's jobs.</summary>
    Task<PrintAckOutcome> AcknowledgeAsync(string agent, string jobId, bool printed, string? reason = null, CancellationToken cancellationToken = default);

    /// <summary>A station's work: jobs for its dialog and bridge printers, and fallbacks of agent jobs that did not print in time.</summary>
    Task<IReadOnlyList<StationPrintJob>> StationQueueAsync(string station, CancellationToken cancellationToken = default);

    /// <summary>
    /// What the station prints for a job: its content for a dialog or bridge printer, or the fallback PDF of an agent job
    /// (which the agent then never receives). Null when the job is not the station's or has nothing to print.
    /// </summary>
    Task<PrintDelivery?> TakeAtStationAsync(string station, string jobId, CancellationToken cancellationToken = default);

    /// <summary>The station's browser says it printed (or could not print) a job it took.</summary>
    Task<PrintAckOutcome> AcknowledgeAtStationAsync(string station, string jobId, bool printed, string? reason = null, CancellationToken cancellationToken = default);

    /// <summary>A new job with the same content and printer (PRD §4.11: a reprint is an explicit new job).</summary>
    Task<PrintSubmitResult> ReprintAsync(string jobId, string? userId, CancellationToken cancellationToken = default);
}

/// <summary>One line of a station's queue.</summary>
/// <param name="Fallback">True for an agent job offered as a PDF because it did not print in time.</param>
[MintPlayer.Spark.Abstractions.SparkContract]
public sealed record StationPrintJob(string Id, string Title, string Kind, string Printer, string Adapter, string? DeviceName, string ContentType, bool Fallback);

internal sealed partial class PrintJobs : IPrintJobs
{
    public const string PdfType = "application/pdf";
    public const string ZplType = "application/zpl";
    public const string ViaFallback = "PdfFallback";

    [Inject] private readonly IDocumentStore documentStore;
    [Inject] private readonly IOptions<SparkPrintingOptions> options;
    [Inject] private readonly IPrintRouter router;
    [Inject] private readonly IEnumerable<PrinterAdapterRegistration> adapters;
    [Inject] private readonly TimeProvider timeProvider;

    public async Task<PrintSubmitResult> SubmitAsync(PrintRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Key);
        var id = PrintJob.IdFor(request.Key);

        using (var probe = documentStore.OpenAsyncSession())
            if (await probe.LoadAsync<PrintJob>(id, cancellationToken) is { } existing)
                return new PrintSubmitResult(PrintSubmitOutcome.Existing, id, existing.Printer);

        var printerName = request.Printer ?? router.Route(request.Kind, request.Site, request.Station);
        if (printerName is null || !options.Value.Printers.TryGetValue(printerName, out var printer))
            return new PrintSubmitResult(PrintSubmitOutcome.NoPrinter, null, Detail: printerName);

        var rendered = Render(request.Content, printer.Format);
        if (rendered is null)
            return new PrintSubmitResult(PrintSubmitOutcome.Unprintable, null, printerName, $"{printer.Format} cannot print this content.");

        var job = new PrintJob
        {
            Id = id,
            Printer = printerName,
            Adapter = printer.Adapter,
            Agent = IsAgent(printer.Adapter) ? printer.Agent : null,
            Address = IsAgent(printer.Adapter) ? printer.Address : null,
            Site = request.Site ?? printer.Site,
            Station = request.Station ?? printer.Station,
            Kind = request.Kind,
            Title = request.Title,
            ContentType = rendered.Value.ContentType,
            Dpi = rendered.Value.Dpi,
            Content = rendered.Value.Content,
            FallbackPdf = rendered.Value.FallbackPdf,
            State = PrintJobState.Queued,
            CreatedAt = timeProvider.GetUtcNow(),
            CreatedBy = request.UserId,
        };
        using var session = documentStore.OpenAsyncSession();
        // Must not exist: two concurrent submits of one key store it once.
        await session.StoreAsync(job, string.Empty, id, cancellationToken);
        try
        {
            await session.SaveChangesAsync(cancellationToken);
        }
        catch (Raven.Client.Exceptions.ConcurrencyException)
        {
            return new PrintSubmitResult(PrintSubmitOutcome.Existing, id, printerName);
        }
        return new PrintSubmitResult(PrintSubmitOutcome.Queued, id, printerName);
    }

    /// <summary>The content for a printer of <paramref name="format"/>, or null when that printer cannot print it.</summary>
    internal static (string ContentType, byte[] Content, int? Dpi, byte[]? FallbackPdf)? Render(PrintContent content, PrintFormat format)
    {
        var dpi = format switch { PrintFormat.Zpl203 => 203, PrintFormat.Zpl300 => 300, _ => (int?)null };
        switch (content)
        {
            case PrintContent.Label label:
                return dpi is { } d
                    ? (ZplType, Encoding.UTF8.GetBytes(LabelRenderer.Zpl(label.Template, d)), d, LabelRenderer.Pdf(label.Template))
                    : (PdfType, LabelRenderer.Pdf(label.Template), null, null);
            case PrintContent.Document document:
                return dpi is null ? (PdfType, document.Pdf, null, null) : null;
            case PrintContent.Carrier carrier when string.Equals(carrier.ContentType, ZplType, StringComparison.OrdinalIgnoreCase):
                return dpi is { } zd && carrier.Dpi == zd ? (ZplType, carrier.Bytes, zd, carrier.FallbackPdf)
                    : dpi is null && carrier.FallbackPdf is { } pdf ? (PdfType, pdf, null, null)
                    : null;
            case PrintContent.Carrier carrier when string.Equals(carrier.ContentType, PdfType, StringComparison.OrdinalIgnoreCase):
                return dpi is null ? (PdfType, carrier.Bytes, null, null) : null;
            default:
                return null;
        }
    }

    public async Task<IReadOnlyList<PrintDelivery>> PullAsync(string agent, int max = 20, CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        var redeliverBefore = now - options.Value.RedeliverAfter;
        List<string> candidates;
        using (var query = documentStore.OpenAsyncSession())
        {
            candidates = await query.Query<PrintJobEntry>(PrintingIndexes.Jobs)
                .Where(e => e.Agent == agent && !e.FallbackTaken
                    && (e.State == PrintJobState.Queued || (e.State == PrintJobState.Delivered && e.DeliveredAt < redeliverBefore)))
                .OrderBy(e => e.CreatedAt)
                .Take(Math.Clamp(max, 1, 100))
                .OfType<PrintJob>()
                .Select(j => j.Id!)
                .ToListAsync(cancellationToken);
        }

        var deliveries = new List<PrintDelivery>();
        foreach (var id in candidates)
        {
            using var session = OpenSession();
            var job = await session.LoadAsync<PrintJob>(id, cancellationToken);
            // The index decided nothing: the loaded job must still be this agent's and due.
            if (job is null || job.Agent != agent || job.FallbackTakenAt is not null
                || !(job.State == PrintJobState.Queued || (job.State == PrintJobState.Delivered && job.DeliveredAt < redeliverBefore)))
                continue;
            job.State = PrintJobState.Delivered;
            job.DeliveredAt = now;
            job.Deliveries++;
            try
            {
                await session.SaveChangesAsync(cancellationToken);
            }
            catch (Raven.Client.Exceptions.ConcurrencyException)
            {
                continue;   // the station took the fallback, or another pull won
            }
            deliveries.Add(new PrintDelivery(job.Id!, job.Printer, job.Address, null, job.ContentType, job.Dpi, job.Content, job.Title));
        }
        return deliveries;
    }

    public async Task<PrintAckOutcome> AcknowledgeAsync(string agent, string jobId, bool printed, string? reason = null, CancellationToken cancellationToken = default)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var session = OpenSession();
            var job = await session.LoadAsync<PrintJob>(jobId, cancellationToken);
            if (job is null || job.Agent != agent)
                return PrintAckOutcome.NotFound;
            if (job.State is PrintJobState.Printed or PrintJobState.Failed)
                return PrintAckOutcome.AlreadyRecorded;
            Finish(job, printed, PrinterAdapters.Agent, reason);
            if (printed && job.FallbackTakenAt is not null)
                job.FailureReason = "The agent printed the job after the station took the PDF fallback: check for a second label.";
            try
            {
                await session.SaveChangesAsync(cancellationToken);
                return PrintAckOutcome.Recorded;
            }
            catch (Raven.Client.Exceptions.ConcurrencyException) when (attempt < 5)
            {
            }
        }
    }

    public async Task<IReadOnlyList<StationPrintJob>> StationQueueAsync(string station, CancellationToken cancellationToken = default)
    {
        var o = options.Value;
        var now = timeProvider.GetUtcNow();
        var stationAdapters = adapters.Where(a => a.DeliveredByStation).Select(a => a.Name).ToList();
        var redeliverBefore = now - o.RedeliverAfter;
        using var session = documentStore.OpenAsyncSession();

        var own = await session.Query<PrintJobEntry>(PrintingIndexes.Jobs)
            .Where(e => e.Station == station && e.Adapter.In(stationAdapters)
                && (e.State == PrintJobState.Queued || (e.State == PrintJobState.Delivered && e.DeliveredAt < redeliverBefore)))
            .OrderBy(e => e.CreatedAt)
            .Take(100)
            .OfType<PrintJob>()
            .ToListAsync(cancellationToken);

        // Agent jobs of this station that did not print in time, or whose agent is offline.
        var fallbackBefore = now - o.FallbackAfter;
        var agentJobs = await session.Query<PrintJobEntry>(PrintingIndexes.Jobs)
            .Where(e => e.Station == station && e.Adapter == PrinterAdapters.Agent && !e.FallbackTaken
                && (e.State == PrintJobState.Queued || e.State == PrintJobState.Delivered))
            .OrderBy(e => e.CreatedAt)
            .Take(100)
            .OfType<PrintJob>()
            .ToListAsync(cancellationToken);
        var offline = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var fallbacks = new List<PrintJob>();
        foreach (var job in agentJobs.Where(j => j.FallbackPdf is not null || j.ContentType == PdfType))
        {
            if (job.Agent is null)
                continue;
            if (!offline.TryGetValue(job.Agent, out var isOffline))
            {
                var status = await session.LoadAsync<PrintAgentStatus>(PrintAgentStatus.IdFor(job.Agent), cancellationToken);
                offline[job.Agent] = isOffline = status?.LastHeartbeatAt is not { } beat || beat < now - o.AgentOfflineAfter;
            }
            if (isOffline || job.CreatedAt < fallbackBefore)
                fallbacks.Add(job);
        }

        return own.Select(j => new StationPrintJob(j.Id!, j.Title, j.Kind, j.Printer, j.Adapter, DeviceName(j.Printer), j.ContentType, false))
            .Concat(fallbacks.Select(j => new StationPrintJob(j.Id!, j.Title, j.Kind, j.Printer, PrinterAdapters.Pdf, null, PdfType, true)))
            .ToList();
    }

    public async Task<PrintDelivery?> TakeAtStationAsync(string station, string jobId, CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        using var session = OpenSession();
        var job = await session.LoadAsync<PrintJob>(jobId, cancellationToken);
        if (job is null || !string.Equals(job.Station, station, StringComparison.OrdinalIgnoreCase)
            || job.State is PrintJobState.Printed or PrintJobState.Failed)
            return null;

        PrintDelivery delivery;
        if (IsAgent(job.Adapter))
        {
            var pdf = job.FallbackPdf ?? (job.ContentType == PdfType ? job.Content : null);
            if (pdf is null)
                return null;
            // From now on the agent never gets it (pull skips taken fallbacks; a concurrent pull conflicts here).
            job.FallbackTakenAt ??= now;
            delivery = new PrintDelivery(job.Id!, job.Printer, null, null, PdfType, null, pdf, job.Title);
        }
        else
        {
            delivery = new PrintDelivery(job.Id!, job.Printer, null, DeviceName(job.Printer), job.ContentType, job.Dpi, job.Content, job.Title);
        }
        job.State = PrintJobState.Delivered;
        job.DeliveredAt = now;
        job.Deliveries++;
        try
        {
            await session.SaveChangesAsync(cancellationToken);
        }
        catch (Raven.Client.Exceptions.ConcurrencyException)
        {
            return null;   // an agent pulled it in the meantime: it prints there
        }
        return delivery;
    }

    public async Task<PrintAckOutcome> AcknowledgeAtStationAsync(string station, string jobId, bool printed, string? reason = null, CancellationToken cancellationToken = default)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var session = OpenSession();
            var job = await session.LoadAsync<PrintJob>(jobId, cancellationToken);
            if (job is null || !string.Equals(job.Station, station, StringComparison.OrdinalIgnoreCase)
                || (IsAgent(job.Adapter) && job.FallbackTakenAt is null))
                return PrintAckOutcome.NotFound;
            if (job.State is PrintJobState.Printed or PrintJobState.Failed)
                return PrintAckOutcome.AlreadyRecorded;
            Finish(job, printed, IsAgent(job.Adapter) ? ViaFallback : job.Adapter, reason);
            try
            {
                await session.SaveChangesAsync(cancellationToken);
                return PrintAckOutcome.Recorded;
            }
            catch (Raven.Client.Exceptions.ConcurrencyException) when (attempt < 5)
            {
            }
        }
    }

    public async Task<PrintSubmitResult> ReprintAsync(string jobId, string? userId, CancellationToken cancellationToken = default)
    {
        using var session = documentStore.OpenAsyncSession();
        var original = await session.LoadAsync<PrintJob>(jobId, cancellationToken);
        if (original is null)
            return new PrintSubmitResult(PrintSubmitOutcome.NoPrinter, null, Detail: jobId);
        var key = $"reprint/{Uri.UnescapeDataString(original.Id![PrintJob.IdPrefix.Length..])}/{Guid.NewGuid():N}";
        var id = PrintJob.IdFor(key);
        var copy = new PrintJob
        {
            Id = id,
            Printer = original.Printer,
            Adapter = original.Adapter,
            Agent = original.Agent,
            Address = original.Address,
            Site = original.Site,
            Station = original.Station,
            Kind = original.Kind,
            Title = original.Title,
            ContentType = original.ContentType,
            Dpi = original.Dpi,
            Content = original.Content,
            FallbackPdf = original.FallbackPdf,
            State = PrintJobState.Queued,
            CreatedAt = timeProvider.GetUtcNow(),
            CreatedBy = userId,
            ReprintOf = original.Id,
        };
        await session.StoreAsync(copy, string.Empty, id, cancellationToken);
        await session.SaveChangesAsync(cancellationToken);
        return new PrintSubmitResult(PrintSubmitOutcome.Queued, id, copy.Printer);
    }

    private void Finish(PrintJob job, bool printed, string via, string? reason)
    {
        var now = timeProvider.GetUtcNow();
        job.State = printed ? PrintJobState.Printed : PrintJobState.Failed;
        if (printed)
        {
            job.PrintedAt = now;
            job.PrintedVia = via;
        }
        else
            job.FailureReason = string.IsNullOrWhiteSpace(reason) ? "Not printed." : reason.Trim()[..Math.Min(reason.Trim().Length, 500)];
        job.ExpiresAtUtc = (now + options.Value.Retention).UtcDateTime;
    }

    private string? DeviceName(string printer) => options.Value.Printers.TryGetValue(printer, out var p) ? p.DeviceName : null;

    private static bool IsAgent(string adapter) => string.Equals(adapter, PrinterAdapters.Agent, StringComparison.OrdinalIgnoreCase);

    private IAsyncDocumentSession OpenSession()
    {
        var session = documentStore.OpenAsyncSession();
        session.Advanced.OptimisticConcurrencyMode = OptimisticConcurrencyMode.Writes;
        return session;
    }
}
